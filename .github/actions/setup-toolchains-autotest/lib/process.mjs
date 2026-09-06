import { spawn } from "node:child_process";
import { constants as fsConstants } from "node:fs";
import { access, realpath } from "node:fs/promises";
import path from "node:path";
import { performance } from "node:perf_hooks";

const OUTPUT_TAIL_LIMIT = 32 * 1024;

export class CommandError extends Error {
    constructor(message, details, options = undefined) {
        super(message, options);
        this.name = "CommandError";
        this.details = details;
    }
}

export function formatCommand(file, args) {
    return [file, ...args].map(quoteArgument).join(" ");
}

export async function runCommand(file, args = [], options = {}) {
    const cwd = options.cwd ?? process.cwd();
    const environment = options.env ?? process.env;
    const capture = options.capture ?? false;
    const display = formatCommand(file, args);
    const startedAt = performance.now();
    console.log(`[command:start] ${display}`);
    console.log(`[command:cwd] ${cwd}`);

    return await new Promise((resolve, reject) => {
        let stdout = "";
        let stderr = "";
        let stdoutTail = "";
        let stderrTail = "";
        let settled = false;
        const child = spawn(file, args, {
            cwd,
            env: environment,
            windowsHide: true,
            shell: false,
            stdio: ["ignore", "pipe", "pipe"],
        });

        child.stdout.on("data", (chunk) => {
            const text = chunk.toString();
            stdoutTail = appendTail(stdoutTail, text);
            if (capture) {
                stdout += text;
            } else {
                process.stdout.write(chunk);
            }
        });
        child.stderr.on("data", (chunk) => {
            const text = chunk.toString();
            stderrTail = appendTail(stderrTail, text);
            if (capture) {
                stderr += text;
            } else {
                process.stderr.write(chunk);
            }
        });
        child.once("error", (error) => {
            if (settled) {
                return;
            }

            settled = true;
            const durationMs = Math.round(performance.now() - startedAt);
            console.error(
                `[command:failure] startError=true durationMs=${durationMs} command=${display}`,
            );
            reject(
                new CommandError(
                    `Could not start command: ${display}`,
                    {
                        file,
                        args,
                        cwd,
                        durationMs,
                        exitCode: null,
                        signal: null,
                        stdoutTail,
                        stderrTail,
                    },
                    { cause: error },
                ),
            );
        });
        child.once("close", (exitCode, signal) => {
            if (settled) {
                return;
            }

            settled = true;
            const durationMs = Math.round(performance.now() - startedAt);
            if (exitCode === 0) {
                console.log(
                    `[command:success] exit=0 durationMs=${durationMs}`,
                );
                resolve({ stdout, stderr, stdoutTail, stderrTail, durationMs });
                return;
            }

            console.error(
                `[command:failure] exit=${exitCode ?? "null"} signal=${signal ?? "none"} durationMs=${durationMs}`,
            );
            reject(
                new CommandError(
                    `Command failed with exit code ${exitCode ?? "null"}: ${display}`,
                    {
                        file,
                        args,
                        cwd,
                        durationMs,
                        exitCode,
                        signal,
                        stdoutTail,
                        stderrTail,
                    },
                ),
            );
        });
    });
}

export async function captureCommand(file, args = [], options = {}) {
    return await runCommand(file, args, { ...options, capture: true });
}

export async function runCommandWithRetries(file, args = [], options = {}) {
    const {
        attempts = 3,
        retryDelayMilliseconds = 2_000,
        ...commandOptions
    } = options;
    if (!Number.isInteger(attempts) || attempts < 1) {
        throw new RangeError(
            `Retry attempts must be a positive integer; received ${attempts}.`,
        );
    }
    if (
        !Number.isFinite(retryDelayMilliseconds) ||
        retryDelayMilliseconds < 0
    ) {
        throw new RangeError(
            `Retry delay must be a non-negative number; received ${retryDelayMilliseconds}.`,
        );
    }

    let lastError = null;
    for (let attempt = 1; attempt <= attempts; attempt += 1) {
        try {
            return await runCommand(file, args, commandOptions);
        } catch (error) {
            lastError = error;
            if (!(error instanceof CommandError) || attempt === attempts) {
                throw error;
            }

            const delayMilliseconds = retryDelayMilliseconds * attempt;
            console.warn(
                `[command:retry] attempt=${attempt} nextAttempt=${attempt + 1} delayMs=${delayMilliseconds} command=${formatCommand(file, args)} reason=${error.message}`,
            );
            await delay(delayMilliseconds);
        }
    }

    throw (
        lastError ??
        new Error(
            `Command retry loop ended unexpectedly: ${formatCommand(file, args)}`,
        )
    );
}

export async function requireCommand(
    names,
    description,
    environment = process.env,
) {
    const matches = await findCommands(names, environment);
    if (matches.length === 0) {
        throw new Error(
            `${description} was not found. Tried: ${names.join(", ")}.`,
        );
    }

    return matches[0];
}

export async function findCommands(names, environment = process.env) {
    const matches = [];
    const seen = new Set();
    for (const name of names) {
        for (const candidate of commandCandidates(name, environment)) {
            if (!(await isExecutableFile(candidate))) {
                continue;
            }

            const resolved = await realpath(candidate);
            const key =
                process.platform === "win32"
                    ? resolved.toLowerCase()
                    : resolved;
            if (!seen.has(key)) {
                seen.add(key);
                matches.push(resolved);
            }
        }
    }

    return matches;
}

export async function getProgramVersion(
    executable,
    args = ["--version"],
    program = undefined,
) {
    const result = await captureCommand(executable, args);
    const output = `${result.stdout}\n${result.stderr}`.trim();
    return parseProgramVersion(
        output,
        program ?? programIdentity(executable),
        executable,
    );
}

export function parseProgramVersion(
    output,
    program = "generic",
    source = program,
) {
    const patterns = versionPatterns(program);
    for (const pattern of patterns) {
        const match = String(output).match(pattern);
        if (match?.groups?.version) {
            return match.groups.version;
        }
    }

    throw new Error(
        `Could not parse the ${program} version reported by '${source}': ${String(output).trim()}`,
    );
}

export async function resolveCompiler(
    candidates,
    major,
    description,
    environment = process.env,
) {
    const paths = await findCommands(candidates, environment);
    const inspected = [];
    for (const executable of paths) {
        const version = await getProgramVersion(executable);
        inspected.push(`${executable} (${version})`);
        if (versionMajor(version) === major) {
            return { path: executable, version };
        }
    }

    const suffix =
        inspected.length === 0
            ? "No candidate executable was found."
            : `Inspected: ${inspected.join("; ")}.`;
    throw new Error(
        `${description} major version ${major} was not found. ${suffix}`,
    );
}

export function compareVersions(left, right) {
    const leftParts = versionParts(left);
    const rightParts = versionParts(right);
    const count = Math.max(leftParts.length, rightParts.length);
    for (let index = 0; index < count; index += 1) {
        const difference = (leftParts[index] ?? 0) - (rightParts[index] ?? 0);
        if (difference !== 0) {
            return difference;
        }
    }

    return 0;
}

export function versionMajor(version) {
    return versionParts(version)[0] ?? 0;
}

function commandCandidates(name, environment) {
    if (path.isAbsolute(name) || name.includes("/") || name.includes("\\")) {
        return [path.resolve(name)];
    }

    const pathValue =
        environment.PATH ?? environment.Path ?? environment.path ?? "";
    const directories = pathValue
        .split(path.delimiter)
        .filter((part) => part.length > 0);
    const extensions =
        process.platform === "win32"
            ? windowsExtensions(name, environment.PATHEXT)
            : [""];
    return directories.flatMap((directory) =>
        extensions.map((extension) => path.join(directory, name + extension)),
    );
}

function windowsExtensions(name, pathExt) {
    if (path.extname(name).length > 0) {
        return [""];
    }

    return (pathExt ?? ".COM;.EXE;.BAT;.CMD")
        .split(";")
        .filter((extension) => extension.length > 0)
        .map((extension) => extension.toLowerCase());
}

async function isExecutableFile(candidate) {
    try {
        await access(
            candidate,
            process.platform === "win32" ? fsConstants.F_OK : fsConstants.X_OK,
        );
        return true;
    } catch {
        return false;
    }
}

function versionParts(version) {
    const match = String(version).match(/\d+(?:\.\d+)*/);
    return match
        ? match[0].split(".").map((part) => Number.parseInt(part, 10))
        : [];
}

function programIdentity(executable) {
    const name = path
        .basename(executable)
        .toLowerCase()
        .replace(/\.(?:exe|cmd|bat)$/i, "");
    if (name === "node") {
        return "node";
    }
    if (name.startsWith("python")) {
        return "python";
    }
    if (name === "wasmtime") {
        return "wasmtime";
    }
    if (name.includes("clang")) {
        return "clang";
    }
    if (name.includes("gcc") || name.includes("g++")) {
        return "gcc";
    }
    return "generic";
}

function versionPatterns(program) {
    const version = "(?<version>\\d+(?:\\.\\d+){1,3})";
    switch (program) {
        case "node":
            return [new RegExp(`^\\s*v${version}\\s*$`, "im")];
        case "python":
            return [new RegExp(`^\\s*Python\\s+${version}(?:\\s|$)`, "im")];
        case "wasmtime":
            return [new RegExp(`^\\s*wasmtime\\s+v?${version}(?:\\s|$)`, "im")];
        case "clang":
            return [
                new RegExp(
                    `(?:Apple\\s+)?clang\\s+version\\s+${version}(?:\\s|$)`,
                    "im",
                ),
            ];
        case "gcc":
            return [
                new RegExp(
                    `(?:gcc|g\\+\\+)(?:[^\\r\\n]*?)\\s+${version}(?:\\s|$)`,
                    "im",
                ),
                new RegExp(`^\\s*(?:gcc|g\\+\\+)\\s+${version}(?:\\s|$)`, "im"),
            ];
        case "generic":
            return [
                new RegExp(
                    `(?<![A-Za-z0-9.])v?${version}(?![A-Za-z0-9.])`,
                    "m",
                ),
            ];
        default:
            throw new Error(`Unknown version parser '${program}'.`);
    }
}

function appendTail(current, addition) {
    const combined = current + addition;
    return combined.length <= OUTPUT_TAIL_LIMIT
        ? combined
        : combined.slice(combined.length - OUTPUT_TAIL_LIMIT);
}

function quoteArgument(value) {
    const text = String(value);
    if (/^[A-Za-z0-9_./:\\=@+-]+$/.test(text)) {
        return text;
    }

    return JSON.stringify(text);
}

async function delay(milliseconds) {
    await new Promise((resolve) => setTimeout(resolve, milliseconds));
}
