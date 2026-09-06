import {
    appendFile,
    mkdir,
    realpath,
    rename,
    rm,
    stat,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { performance } from "node:perf_hooks";
import { CommandError, runCommand } from "./process.mjs";

const PROFILE_HOSTS = Object.freeze({
    "windows-vs2022": {
        platform: "win32",
        architecture: "x64",
        imageLabel: "windows-2022",
    },
    "windows-vs2026": {
        platform: "win32",
        architecture: "x64",
        imageLabel: "windows-2025-vs2026",
    },
    "ubuntu-24.04": {
        platform: "linux",
        architecture: "x64",
        imageLabel: "ubuntu-24.04",
    },
    "macos-15-arm64": {
        platform: "darwin",
        architecture: "arm64",
        imageLabel: "macos-15",
    },
});

export class SetupContext {
    constructor({ profile, outputPath, toolchainRoot, workspace }) {
        const host = PROFILE_HOSTS[profile];
        if (!host) {
            throw new Error(`Unknown profile '${profile}'.`);
        }

        this.profile = profile;
        this.host = host;
        this.workspace = path.resolve(workspace);
        this.outputPath = path.resolve(outputPath);
        this.toolchainRoot = path.resolve(toolchainRoot);
        this.downloadsRoot = path.join(this.toolchainRoot, "downloads");
        this.setupReportPath =
            this.outputPath.replace(/\.json$/i, "") + ".setup.json";
        this.installations = [];
        this.runtimes = [];
        this.stageStack = [];
        this.stageResults = [];
        this.startedAt = new Date().toISOString();
    }

    async initialize() {
        if (process.platform !== this.host.platform) {
            throw new Error(
                `Profile '${this.profile}' requires platform ${this.host.platform}; current platform is ${process.platform}.`,
            );
        }
        if (process.arch !== this.host.architecture) {
            throw new Error(
                `Profile '${this.profile}' requires architecture ${this.host.architecture}; current architecture is ${process.arch}.`,
            );
        }

        await this.requirePath(this.workspace, "workspace", "directory");
        await mkdir(this.toolchainRoot, { recursive: true });
        await mkdir(this.downloadsRoot, { recursive: true });
        await mkdir(path.dirname(this.outputPath), { recursive: true });
        console.log(
            `[setup] node=${process.version} platform=${process.platform} architecture=${process.arch}`,
        );
        console.log(`[setup] workspace=${this.workspace}`);
        console.log(`[setup] toolchainRoot=${this.toolchainRoot}`);
        console.log(`[setup] manifest=${this.outputPath}`);
    }

    async stage(name, action) {
        const startedAt = performance.now();
        this.stageStack.push(name);
        const stagePath = [...this.stageStack];
        const result = {
            name,
            path: stagePath,
            status: "running",
            durationMs: null,
            error: null,
        };
        this.stageResults.push(result);
        console.log(`::group::Setup: ${name}`);
        console.log(`[stage:start] ${stagePath.join(" > ")}`);
        try {
            const value = await action();
            result.status = "succeeded";
            result.durationMs = Math.round(performance.now() - startedAt);
            console.log(
                `[stage:success] ${name} durationMs=${result.durationMs}`,
            );
            return value;
        } catch (error) {
            result.status = "failed";
            result.durationMs = Math.round(performance.now() - startedAt);
            result.error = serializeError(error);
            if (
                error &&
                typeof error === "object" &&
                !Array.isArray(error.setupStages)
            ) {
                error.setupStages = stagePath;
            }
            console.error(
                `[stage:failure] ${name} durationMs=${result.durationMs}`,
            );
            throw error;
        } finally {
            this.stageStack.pop();
            console.log("::endgroup::");
        }
    }

    assertToolchainPath(candidate) {
        const resolved = path.resolve(candidate);
        const relative = path.relative(this.toolchainRoot, resolved);
        if (
            relative === "" ||
            (!relative.startsWith(`..${path.sep}`) &&
                relative !== ".." &&
                !path.isAbsolute(relative))
        ) {
            return resolved;
        }

        throw new Error(
            `Refusing to modify '${resolved}' because it is outside '${this.toolchainRoot}'.`,
        );
    }

    async resetToolchainDirectory(candidate) {
        const resolved = this.assertToolchainPath(candidate);
        await rm(resolved, { recursive: true, force: true });
        await mkdir(resolved, { recursive: true });
        return resolved;
    }

    async requirePath(candidate, description, expectedType = undefined) {
        const resolved = path.resolve(candidate);
        let information;
        try {
            information = await stat(resolved);
        } catch (error) {
            throw new Error(`${description} was not found at '${resolved}'.`, {
                cause: error,
            });
        }

        if (expectedType === "file" && !information.isFile()) {
            throw new Error(`${description} at '${resolved}' is not a file.`);
        }
        if (expectedType === "directory" && !information.isDirectory()) {
            throw new Error(
                `${description} at '${resolved}' is not a directory.`,
            );
        }

        return await realpath(resolved);
    }

    addInstallation({
        id,
        kind,
        rootPath,
        version,
        environment = {},
        sourceUri = null,
        sha256 = null,
        revision = null,
    }) {
        this.installations.push({
            id,
            kind,
            rootPath: path.resolve(rootPath),
            version,
            environment,
            sourceUri,
            sha256,
            revision,
        });
        console.log(
            `[installation] id=${id} kind=${kind} version=${version} root=${path.resolve(rootPath)}`,
        );
    }

    addRuntime({
        id,
        kind,
        runtimePath,
        version,
        installationId = null,
        sourceUri = null,
        sha256 = null,
        revision = null,
    }) {
        this.runtimes.push({
            id,
            kind,
            path: path.resolve(runtimePath),
            version,
            installationId,
            sourceUri,
            sha256,
            revision,
        });
        console.log(
            `[runtime] id=${id} kind=${kind} version=${version} path=${path.resolve(runtimePath)}`,
        );
    }

    async writeManifest() {
        const manifest = {
            schemaVersion: 1,
            profile: this.profile,
            runner: {
                imageLabel: this.host.imageLabel,
                imageOS: process.env.ImageOS ?? "",
                imageVersion: process.env.ImageVersion ?? "",
                os: process.env.RUNNER_OS ?? platformName(),
                architecture: process.env.RUNNER_ARCH ?? architectureName(),
            },
            environment: {},
            installations: this.installations,
            runtimes: this.runtimes,
        };
        await writeJsonAtomically(this.outputPath, manifest);
        console.log(
            `[manifest] installations=${this.installations.length} runtimes=${this.runtimes.length}`,
        );
    }

    async writeSetupReport(status, error = null) {
        const report = {
            schemaVersion: 1,
            profile: this.profile,
            status,
            startedAt: this.startedAt,
            completedAt: new Date().toISOString(),
            host: {
                platform: process.platform,
                architecture: process.arch,
                nodeVersion: process.version,
                imageOS: process.env.ImageOS ?? "",
                imageVersion: process.env.ImageVersion ?? "",
            },
            paths: {
                workspace: this.workspace,
                toolchainRoot: this.toolchainRoot,
                manifest: this.outputPath,
            },
            stages: this.stageResults,
            failure: error === null ? null : serializeError(error),
            installations: this.installations,
            runtimes: this.runtimes,
        };
        await writeJsonAtomically(this.setupReportPath, report);
        console.log(`[setup:report] ${this.setupReportPath}`);
    }

    async buildAutoTest() {
        const project = path.join(
            this.workspace,
            "Tests",
            "Incant.AutoTest.CppToolchain",
            "Incant.AutoTest.CppToolchain.csproj",
        );
        await this.requirePath(project, "AutoTest project", "file");
        await runCommand("dotnet", ["restore", project], {
            cwd: this.workspace,
        });
        await runCommand(
            "dotnet",
            ["build", project, "--configuration", "Release", "--no-restore"],
            { cwd: this.workspace },
        );
    }

    async exportActionEnvironment() {
        const appHostDirectory = await this.requirePath(
            path.join(
                this.workspace,
                "build",
                "bin",
                "Incant.AutoTest.CppToolchain",
                "release",
            ),
            "AutoTest apphost directory",
            "directory",
        );
        const manifest = await realpath(this.outputPath);
        if (process.env.GITHUB_PATH) {
            await appendFile(
                process.env.GITHUB_PATH,
                `${singleLine(appHostDirectory)}\n`,
                "utf8",
            );
        }
        if (process.env.GITHUB_ENV) {
            await appendFile(
                process.env.GITHUB_ENV,
                `INCANT_AUTOTEST_ENVIRONMENT=${singleLine(manifest)}\n`,
                "utf8",
            );
        }
        console.log(`[action] apphostDirectory=${appHostDirectory}`);
        console.log(`[action] INCANT_AUTOTEST_ENVIRONMENT=${manifest}`);
    }
}

export function reportSetupFailure(error, profile = "unknown") {
    const message = error instanceof Error ? error.message : String(error);
    const stages =
        error && typeof error === "object" && Array.isArray(error.setupStages)
            ? error.setupStages.join(" > ")
            : "initialization";
    console.error(`[setup:error] profile=${profile}`);
    console.error(`[setup:error] stages=${stages}`);
    console.error(`[setup:error] ${message}`);

    const commandError = errorChain(error).find(
        (candidate) => candidate instanceof CommandError,
    );
    if (commandError instanceof CommandError) {
        const details = commandError.details;
        console.error(
            `[setup:error] command=${details.file} ${details.args.map(JSON.stringify).join(" ")}`,
        );
        console.error(`[setup:error] cwd=${details.cwd}`);
        console.error(
            `[setup:error] exitCode=${details.exitCode} signal=${details.signal ?? "none"} durationMs=${details.durationMs}`,
        );
        if (details.stdoutTail.trim().length > 0) {
            console.error("[setup:error] stdout tail:");
            console.error(details.stdoutTail.trimEnd());
        }
        if (details.stderrTail.trim().length > 0) {
            console.error("[setup:error] stderr tail:");
            console.error(details.stderrTail.trimEnd());
        }
    }
    if (error instanceof Error && error.stack) {
        console.error(error.stack);
    }

    const causes = errorChain(error).slice(1);
    for (let index = 0; index < causes.length; index += 1) {
        const cause = causes[index];
        console.error(
            `[setup:error] cause[${index + 1}]=${cause instanceof Error ? cause.message : String(cause)}`,
        );
    }

    const rootCause = causes.at(-1);
    const rootMessage =
        rootCause instanceof Error
            ? rootCause.message
            : rootCause
              ? String(rootCause)
              : message;
    const annotation = escapeWorkflowCommand(
        `Profile ${profile} failed in ${stages}: ${message}${rootMessage === message ? "" : ` Cause: ${rootMessage}`}`,
    );
    console.error(`::error title=Toolchain setup failed::${annotation}`);
}

function serializeError(error) {
    if (!(error instanceof Error)) {
        return { name: "NonError", message: String(error) };
    }

    const result = {
        name: error.name,
        message: error.message,
        stack: error.stack ?? null,
    };
    if (error instanceof CommandError) {
        result.command = error.details;
    }
    if (error.cause !== undefined) {
        result.cause = serializeError(error.cause);
    }
    return result;
}

function errorChain(error) {
    const result = [];
    const visited = new Set();
    let current = error;
    while (current !== undefined && current !== null && !visited.has(current)) {
        result.push(current);
        if (!(current instanceof Error)) {
            break;
        }
        visited.add(current);
        current = current.cause;
    }
    return result;
}

async function writeJsonAtomically(destination, value) {
    await mkdir(path.dirname(destination), { recursive: true });
    const temporary = `${destination}.${process.pid}.${Date.now()}.tmp`;
    try {
        await writeFile(temporary, `${JSON.stringify(value, null, 2)}\n`, {
            encoding: "utf8",
            flag: "wx",
        });
        await rename(temporary, destination);
    } finally {
        await rm(temporary, { force: true });
    }
}

function platformName() {
    return (
        { win32: "Windows", linux: "Linux", darwin: "macOS" }[
            process.platform
        ] ?? process.platform
    );
}

function architectureName() {
    return { x64: "X64", arm64: "ARM64" }[process.arch] ?? process.arch;
}

function singleLine(value) {
    if (value.includes("\r") || value.includes("\n")) {
        throw new Error(
            "GitHub environment file values must not contain line breaks.",
        );
    }

    return value;
}

function escapeWorkflowCommand(value) {
    return String(value)
        .replaceAll("%", "%25")
        .replaceAll("\r", "%0D")
        .replaceAll("\n", "%0A")
        .replaceAll(":", "%3A")
        .replaceAll(",", "%2C");
}
