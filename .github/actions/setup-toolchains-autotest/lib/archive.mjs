import { createHash } from "node:crypto";
import { createReadStream, createWriteStream } from "node:fs";
import {
    copyFile,
    mkdir,
    readFile,
    readdir,
    rename,
    rm,
    stat,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { Readable, Transform } from "node:stream";
import { pipeline } from "node:stream/promises";
import { requireCommand, runCommand } from "./process.mjs";

const DOWNLOAD_ATTEMPTS = 3;
const DOWNLOAD_INACTIVITY_TIMEOUT_MS = 2 * 60 * 1_000;
const ARCHIVE_COMPLETION_FILE = ".incant-archive-ready.json";
const PROGRESS_INTERVAL_BYTES = 32 * 1024 * 1024;
const PROGRESS_INTERVAL_MS = 15_000;

export async function getVerifiedDownload(
    context,
    uri,
    sha256,
    fileName = undefined,
) {
    const source = validateDownload(uri, sha256);
    const cacheName = fileName ?? path.posix.basename(source.pathname);
    if (!cacheName || path.basename(cacheName) !== cacheName) {
        throw new Error(
            `Download cache name '${cacheName}' must be a file name.`,
        );
    }

    const expectedHash = sha256.toLowerCase();
    const archive = context.assertToolchainPath(
        path.join(context.downloadsRoot, cacheName),
    );
    if (await isFile(archive)) {
        const actualHash = await hashFile(archive);
        if (actualHash === expectedHash) {
            const information = await stat(archive);
            console.log(
                `[download:cache-hit] file=${archive} bytes=${information.size} sha256=${actualHash}`,
            );
            return archive;
        }

        console.warn(
            `[download:cache-invalid] file=${archive} expectedSha256=${expectedHash} actualSha256=${actualHash}`,
        );
        await rm(archive, { force: true });
    }

    const temporary = context.assertToolchainPath(`${archive}.downloading`);
    for (let attempt = 1; attempt <= DOWNLOAD_ATTEMPTS; attempt += 1) {
        await rm(temporary, { force: true });
        try {
            await downloadFile(source, temporary, expectedHash, attempt);
            await rename(temporary, archive);
            return archive;
        } catch (error) {
            await rm(temporary, { force: true });
            if (attempt === DOWNLOAD_ATTEMPTS) {
                throw new Error(
                    `Could not download '${source}' after ${DOWNLOAD_ATTEMPTS} attempts.`,
                    { cause: error },
                );
            }

            const delayMs = attempt * 2_000;
            console.warn(
                `[download:retry] attempt=${attempt} nextAttempt=${attempt + 1} delayMs=${delayMs} reason=${errorMessage(error)}`,
            );
            await delay(delayMs);
        }
    }

    throw new Error(`Could not download '${source}'.`);
}

export async function copyDownloadTo(source, destination) {
    await mkdir(path.dirname(destination), { recursive: true });
    await copyFile(source, destination);
    console.log(`[download:copy] source=${source} destination=${destination}`);
}

export async function expandToolArchive(
    context,
    archive,
    destination,
    probePath,
    sha256,
) {
    const resolvedDestination = context.assertToolchainPath(destination);
    if (!/^[0-9a-f]{64}$/i.test(sha256)) {
        throw new Error(
            `Archive '${archive}' has an invalid SHA-256 value '${sha256}'.`,
        );
    }
    const completion = archiveCompletion(sha256, probePath);
    if (await isArchiveReady(resolvedDestination, probePath, completion)) {
        console.log(
            `[archive:cache-hit] destination=${resolvedDestination} probe=${probePath}`,
        );
        return await context.requirePath(
            resolvedDestination,
            "archive destination",
            "directory",
        );
    }

    const staging = context.assertToolchainPath(
        `${resolvedDestination}.extracting`,
    );
    await context.resetToolchainDirectory(staging);
    try {
        const tar = await requireCommand(["tar"], "tar");
        console.log(`[archive:extract] archive=${archive} staging=${staging}`);
        await runCommand(tar, ["-xf", archive, "-C", staging]);

        const source = await locateArchiveRoot(staging, probePath);
        if (!source) {
            throw new Error(
                `Archive '${archive}' does not contain '${probePath}'.`,
            );
        }

        await context.resetToolchainDirectory(resolvedDestination);
        const entries = await readdir(source, { withFileTypes: true });
        for (const entry of entries) {
            await rename(
                path.join(source, entry.name),
                path.join(resolvedDestination, entry.name),
            );
        }

        await context.requirePath(
            path.join(resolvedDestination, probePath),
            `archive probe '${probePath}'`,
            "file",
        );
        await writeFile(
            path.join(resolvedDestination, ARCHIVE_COMPLETION_FILE),
            completion,
            "utf8",
        );
        console.log(
            `[archive:ready] destination=${resolvedDestination} probe=${probePath}`,
        );
        return await context.requirePath(
            resolvedDestination,
            "archive destination",
            "directory",
        );
    } finally {
        await rm(staging, { recursive: true, force: true });
    }
}

async function isArchiveReady(destination, probePath, completion) {
    if (
        !(await isFile(path.join(destination, probePath))) ||
        !(await isFile(path.join(destination, ARCHIVE_COMPLETION_FILE)))
    ) {
        return false;
    }

    try {
        return (
            (await readFile(
                path.join(destination, ARCHIVE_COMPLETION_FILE),
                "utf8",
            )) === completion
        );
    } catch {
        return false;
    }
}

function archiveCompletion(sha256, probePath) {
    return `${JSON.stringify({
        schemaVersion: 1,
        archiveSha256: sha256.toLowerCase(),
        probePath,
    })}\n`;
}

async function downloadFile(source, destination, expectedHash, attempt) {
    console.log(
        `[download:start] uri=${source} attempt=${attempt}/${DOWNLOAD_ATTEMPTS}`,
    );
    console.log(
        `[download:expected] sha256=${expectedHash} destination=${destination}`,
    );
    const startedAt = Date.now();
    const controller = new AbortController();
    let inactivityTimer = null;
    let timedOut = false;
    const resetInactivityTimer = () => {
        clearTimeout(inactivityTimer);
        inactivityTimer = setTimeout(() => {
            timedOut = true;
            controller.abort();
        }, DOWNLOAD_INACTIVITY_TIMEOUT_MS);
    };

    try {
        resetInactivityTimer();
        const response = await fetch(source, {
            redirect: "follow",
            headers: { "User-Agent": "Incant-Toolchain-AutoTest-Setup/1" },
            signal: controller.signal,
        });
        if (!response.ok || !response.body) {
            throw new Error(
                `Download '${source}' returned HTTP ${response.status} ${response.statusText}.`,
            );
        }

        const declaredLength = parseContentLength(
            response.headers.get("content-length"),
        );
        console.log(
            `[download:response] status=${response.status} finalUri=${response.url} contentLength=${declaredLength ?? "unknown"}`,
        );
        let receivedBytes = 0;
        let lastReportedBytes = 0;
        let lastReportedAt = Date.now();
        const progress = new Transform({
            transform(chunk, _encoding, callback) {
                resetInactivityTimer();
                receivedBytes += chunk.length;
                const now = Date.now();
                if (
                    receivedBytes - lastReportedBytes >=
                        PROGRESS_INTERVAL_BYTES ||
                    now - lastReportedAt >= PROGRESS_INTERVAL_MS
                ) {
                    console.log(
                        `[download:progress] bytes=${receivedBytes} total=${declaredLength ?? "unknown"} elapsedMs=${now - startedAt}`,
                    );
                    lastReportedBytes = receivedBytes;
                    lastReportedAt = now;
                }
                callback(null, chunk);
            },
        });

        await pipeline(
            Readable.fromWeb(response.body),
            progress,
            createWriteStream(destination, { flags: "wx" }),
        );
        clearTimeout(inactivityTimer);
        inactivityTimer = null;
        if (
            declaredLength !== null &&
            response.headers.get("content-encoding") === null &&
            receivedBytes !== declaredLength
        ) {
            throw new Error(
                `Download '${source}' was truncated: expected ${declaredLength} bytes, received ${receivedBytes}.`,
            );
        }

        const actualHash = await hashFile(destination);
        if (actualHash !== expectedHash) {
            throw new Error(
                `SHA-256 mismatch for '${source}': expected ${expectedHash}, found ${actualHash}.`,
            );
        }

        console.log(
            `[download:success] bytes=${receivedBytes} sha256=${actualHash} durationMs=${Date.now() - startedAt}`,
        );
    } catch (error) {
        if (timedOut) {
            throw new Error(
                `Download '${source}' received no data for ${DOWNLOAD_INACTIVITY_TIMEOUT_MS} ms.`,
                { cause: error },
            );
        }
        throw error;
    } finally {
        clearTimeout(inactivityTimer);
    }
}

async function hashFile(file) {
    const hash = createHash("sha256");
    for await (const chunk of createReadStream(file)) {
        hash.update(chunk);
    }
    return hash.digest("hex");
}

async function locateArchiveRoot(staging, probePath) {
    if (await isFile(path.join(staging, probePath))) {
        return staging;
    }

    const entries = await readdir(staging, { withFileTypes: true });
    const directories = entries
        .filter((entry) => entry.isDirectory())
        .map((entry) => entry.name)
        .sort((left, right) => left.localeCompare(right));
    for (const directory of directories) {
        const candidate = path.join(staging, directory);
        if (await isFile(path.join(candidate, probePath))) {
            return candidate;
        }
    }

    return null;
}

function validateDownload(uri, sha256) {
    let source;
    try {
        source = new URL(uri);
    } catch (error) {
        throw new Error(`Download URI '${uri}' is invalid.`, { cause: error });
    }
    if (source.protocol !== "https:") {
        throw new Error(`Download URI '${uri}' must use HTTPS.`);
    }
    if (!/^[0-9a-f]{64}$/i.test(sha256)) {
        throw new Error(
            `Download '${uri}' has an invalid SHA-256 value '${sha256}'.`,
        );
    }
    return source;
}

async function isFile(candidate) {
    try {
        return (await stat(candidate)).isFile();
    } catch {
        return false;
    }
}

function parseContentLength(value) {
    if (!value || !/^\d+$/.test(value)) {
        return null;
    }
    return Number.parseInt(value, 10);
}

function errorMessage(error) {
    return error instanceof Error ? error.message : String(error);
}

async function delay(milliseconds) {
    await new Promise((resolve) => setTimeout(resolve, milliseconds));
}
