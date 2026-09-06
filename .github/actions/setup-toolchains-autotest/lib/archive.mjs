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
    probePaths,
    sha256,
) {
    const probes = normalizeProbePaths(probePaths);
    const resolvedDestination = context.assertToolchainPath(destination);
    if (!/^[0-9a-f]{64}$/i.test(sha256)) {
        throw new Error(
            `Archive '${archive}' has an invalid SHA-256 value '${sha256}'.`,
        );
    }
    const completion = archiveCompletion(sha256, probes);
    if (await isArchiveReady(resolvedDestination, probes, completion)) {
        console.log(
            `[archive:cache-hit] destination=${resolvedDestination} probes=${probes.join(",")}`,
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
        const extractor = await selectArchiveExtractor(archive);
        console.log(
            `[archive:extract] format=${extractor.format} backend=${extractor.executable} archive=${archive} staging=${staging}`,
        );
        await runCommand(
            extractor.executable,
            extractor.arguments(archive, staging),
        );

        const source = await locateArchiveRoot(staging, probes[0]);
        if (!source) {
            throw new Error(
                `Archive '${archive}' does not contain root probe '${probes[0]}'.`,
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

        for (const probe of probes) {
            await context.requirePath(
                path.join(resolvedDestination, probe),
                `archive probe '${probe}'`,
                "file",
            );
        }
        await writeFile(
            path.join(resolvedDestination, ARCHIVE_COMPLETION_FILE),
            completion,
            "utf8",
        );
        console.log(
            `[archive:ready] destination=${resolvedDestination} probes=${probes.join(",")}`,
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

async function selectArchiveExtractor(archive) {
    const format = archiveFormat(archive);
    if (format === "zip" && process.platform !== "win32") {
        const unzip = await requireCommand(["unzip"], "ZIP extractor");
        return {
            format,
            executable: unzip,
            arguments: (source, destination) => [
                "-q",
                source,
                "-d",
                destination,
            ],
        };
    }

    const tar =
        process.platform === "win32"
            ? await requireWindowsSystemTar()
            : await requireCommand(["tar"], "tar archive extractor");
    return {
        format,
        executable: tar,
        arguments: (source, destination) => ["-xf", source, "-C", destination],
    };
}

function archiveFormat(archive) {
    const name = path.basename(archive).toLowerCase();
    if (name.endsWith(".zip")) {
        return "zip";
    }
    if (
        name.endsWith(".tar.gz") ||
        name.endsWith(".tgz") ||
        name.endsWith(".tar.xz") ||
        name.endsWith(".txz") ||
        name.endsWith(".tar")
    ) {
        return "tar";
    }

    throw new Error(
        `Archive '${archive}' has an unsupported format. Expected ZIP, tar, tar.gz, or tar.xz.`,
    );
}

async function requireWindowsSystemTar() {
    const windowsRoot = process.env.SystemRoot ?? process.env.WINDIR;
    if (!windowsRoot) {
        throw new Error(
            "The Windows system directory could not be resolved for archive extraction.",
        );
    }

    const executable = path.join(windowsRoot, "System32", "tar.exe");
    if (!(await isFile(executable))) {
        throw new Error(
            `The Windows archive extractor was not found at '${executable}'.`,
        );
    }

    return executable;
}

async function isArchiveReady(destination, probePaths, completion) {
    if (!(await isFile(path.join(destination, ARCHIVE_COMPLETION_FILE)))) {
        return false;
    }
    for (const probePath of probePaths) {
        if (!(await isFile(path.join(destination, probePath)))) {
            return false;
        }
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

function archiveCompletion(sha256, probePaths) {
    return `${JSON.stringify({
        schemaVersion: 2,
        archiveSha256: sha256.toLowerCase(),
        probePaths,
    })}\n`;
}

function normalizeProbePaths(probePaths) {
    const values = Array.isArray(probePaths) ? probePaths : [probePaths];
    if (values.length === 0) {
        throw new Error("At least one archive probe path is required.");
    }

    const result = [];
    const seen = new Set();
    for (const value of values) {
        if (typeof value !== "string" || value.trim().length === 0) {
            throw new Error("Archive probe paths must be non-empty strings.");
        }

        const normalized = path.normalize(value);
        if (
            path.isAbsolute(normalized) ||
            normalized === ".." ||
            normalized.startsWith(`..${path.sep}`)
        ) {
            throw new Error(
                `Archive probe path '${value}' must remain inside the archive root.`,
            );
        }

        const key =
            process.platform === "win32"
                ? normalized.toLowerCase()
                : normalized;
        if (!seen.has(key)) {
            seen.add(key);
            result.push(normalized);
        }
    }

    return result;
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
