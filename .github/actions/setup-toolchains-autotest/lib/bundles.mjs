import { readdir, readFile, realpath, stat } from "node:fs/promises";
import path from "node:path";
import {
    copyDownloadTo,
    expandToolArchive,
    getVerifiedDownload,
} from "./archive.mjs";
import {
    captureCommand,
    CommandError,
    getProgramVersion,
    requireCommand,
    runCommand,
    runCommandWithRetries,
} from "./process.mjs";

const EMSDK_REVISION = "5eb0bde7585670252e8ba05e9d361627bffd08b5";
const EMSDK_URI = "https://github.com/emscripten-core/emsdk.git";
const EMSCRIPTEN_PACKAGE_ROOT =
    "https://storage.googleapis.com/webassembly/emscripten-releases-builds";

const ANDROID_RELEASES = Object.freeze([
    { id: "android-25.2.9519653", version: "25.2.9519653", release: "r25c" },
    { id: "android-27.2.12479018", version: "27.2.12479018", release: "r27c" },
]);

const ANDROID_HASHES = Object.freeze({
    "r25c:windows":
        "f70093964f6cbbe19268f9876a20f92d3a593db3ad2037baadd25fd8d71e84e2",
    "r25c:linux":
        "769ee342ea75f80619d985c2da990c48b3d8eaf45f48783a2d48870d04b46108",
    "r25c:darwin":
        "b01bae969a5d0bfa0da18469f650a1628dc388672f30e0ba231da5c74245bc92",
    "r27c:windows":
        "27e49f11e0cee5800983d8af8f4acd5bf09987aa6f790d4439dda9f3643d2494",
    "r27c:linux":
        "59c2f6dc96743b5daf5d1626684640b20a6bd2b1d85b13156b90333741bad5cc",
    "r27c:darwin":
        "8c5685457c58a88527367d46d3f14e8c727d962c39f85344cff0c0768a73c3b7",
});

const EMSCRIPTEN_RELEASES = Object.freeze([
    {
        version: "3.1.64",
        releaseRevision: "fd61bacaf40131f74987e649a135f1dd559aff60",
    },
    {
        version: "6.0.9",
        releaseRevision: "f04ea239d533260dd1db760dd2d668d5f9a88d6b",
    },
]);

const EMSCRIPTEN_RELEASE_HASHES = Object.freeze({
    "3.1.64:windows":
        "eb5b59afb420915daab4c383e5f73d456cc14776dce02fdc852c46522cda5531",
    "3.1.64:linux":
        "c39de24beca60fd580f6dff0eca0e275016042a30234588b19eda82397e299f3",
    "3.1.64:macos-arm64":
        "47449057c345a09aa8750be1a357c364ffea9f8a066066cb341a7a2a14bac96a",
    "6.0.9:windows":
        "f7512eab6e69ad9d7de5adbf39e68d7d6773b317b13e70ec5003ef1d10f92980",
    "6.0.9:linux":
        "d5c6c2917fbc1cae1a7d1e581f1c0b2817369dd57f94c7a0d05921476f1a7287",
    "6.0.9:macos-arm64":
        "b60514308507f64f4138d3c55bdb6979f20222288700fde603dced23b65dd533",
});

const WASI_HASHES = Object.freeze({
    "33:x86_64-linux":
        "0ba8b5bfaeb2adf3f29bab5841d76cf5318ab8e1642ea195f88baba1abd47bce",
    "33:arm64-macos":
        "85c997a2665ead91673b5bb88b7d0df3fc8900df3bfa244f720d478187bbdc78",
    "33:x86_64-windows":
        "df14ca2a2127c2d6b6be07e6f5549b3af9c1b3c0112430c200a4749970c59f06",
    "34:x86_64-linux":
        "b761e3a0721dbae9c09a0059e5fdb2bf917d1b4a8a7b430fb3b5aafb0984b2c4",
    "34:arm64-macos":
        "9c59398106b417f8f14913380fdf0097a8cc0ff4af9eb3ce0065a859e88d49e9",
    "34:x86_64-windows":
        "cccb5c323a9b34f0349a9b09e8804a0a7632c68c3310f4b5f437ed57d7e71d8f",
});

export async function prepareBundleToolchains(context) {
    if (context.profile === "windows-vs2026") {
        console.log(
            "[bundles] This profile does not require cross-platform bundles.",
        );
        return;
    }

    for (const release of ANDROID_RELEASES) {
        await context.stage(`Android NDK ${release.version}`, async () => {
            await prepareAndroidNdk(context, release);
        });
    }
    for (const release of EMSCRIPTEN_RELEASES) {
        await context.stage(`Emscripten ${release.version}`, async () => {
            await prepareEmscripten(context, release);
        });
    }
    for (const version of [33, 34]) {
        await context.stage(`WASI SDK ${version}`, async () => {
            await prepareWasiSdk(context, version);
        });
    }
    await context.stage("Wasmtime 45.0.0", async () => {
        await prepareWasmtime(context);
    });
}

async function prepareAndroidNdk(context, release) {
    const platform = hostValue({
        win32: "windows",
        darwin: "darwin",
        linux: "linux",
    });
    const hashKey = `${release.release}:${platform}`;
    const sha256 = requireHash(
        ANDROID_HASHES,
        hashKey,
        `Android NDK ${release.release}`,
    );
    const uri = `https://dl.google.com/android/repository/android-ndk-${release.release}-${platform}.zip`;
    const archive = await getVerifiedDownload(context, uri, sha256);
    const ndkRoot = await expandToolArchive(
        context,
        archive,
        path.join(context.toolchainRoot, `android-ndk-${release.version}`),
        "source.properties",
        sha256,
    );
    const metadataPath = await context.requirePath(
        path.join(ndkRoot, "source.properties"),
        `Android NDK ${release.version} metadata`,
        "file",
    );
    const metadata = await readFile(metadataPath, "utf8");
    const revision = metadata.match(
        /^\s*Pkg\.Revision\s*=\s*(?<version>\S+)\s*$/m,
    )?.groups?.version;
    if (revision !== release.version) {
        throw new Error(
            `Android NDK archive '${uri}' reports revision '${revision ?? "missing"}'; expected '${release.version}'.`,
        );
    }

    context.addInstallation({
        id: release.id,
        kind: "AndroidNdk",
        rootPath: ndkRoot,
        version: release.version,
        environment: {
            ANDROID_NDK_HOME: ndkRoot,
            ANDROID_NDK_ROOT: ndkRoot,
        },
        sourceUri: uri,
        sha256,
        revision: release.release,
    });
}

async function prepareEmscripten(context, release) {
    const id = `emscripten-${release.version}`;
    const emsdkRoot = path.join(
        context.toolchainRoot,
        `emsdk-${release.version}`,
    );
    const git = await requireCommand(["git"], "git");
    const bootstrapPython = await requireCommand(
        process.platform === "win32"
            ? ["python.exe", "python"]
            : ["python3", "python"],
        "Python for emsdk",
    );

    await prepareEmsdkCheckout(context, emsdkRoot, git);
    const checkout = await captureCommand(git, [
        "-C",
        emsdkRoot,
        "rev-parse",
        "HEAD",
    ]);
    const actualEmsdkRevision = checkout.stdout.trim();
    if (actualEmsdkRevision !== EMSDK_REVISION) {
        throw new Error(
            `emsdk at '${emsdkRoot}' is revision '${actualEmsdkRevision}'; expected '${EMSDK_REVISION}'.`,
        );
    }

    const releaseTagsPath = await context.requirePath(
        path.join(emsdkRoot, "emscripten-releases-tags.json"),
        "emsdk release manifest",
        "file",
    );
    const releaseTags = JSON.parse(await readFile(releaseTagsPath, "utf8"));
    const actualReleaseRevision = releaseTags?.releases?.[release.version];
    if (actualReleaseRevision === undefined) {
        throw new Error(
            `Emscripten ${release.version} is absent from emsdk revision ${EMSDK_REVISION}.`,
        );
    }
    if (actualReleaseRevision !== release.releaseRevision) {
        throw new Error(
            `Emscripten ${release.version} resolves to ${actualReleaseRevision} at emsdk revision ${EMSDK_REVISION}; expected ${release.releaseRevision}.`,
        );
    }

    const host = emscriptenHost();
    const releaseSha256 = requireHash(
        EMSCRIPTEN_RELEASE_HASHES,
        `${release.version}:${host.key}`,
        `Emscripten ${release.version}`,
    );
    const releaseUri = `${EMSCRIPTEN_PACKAGE_ROOT}/${host.releasePlatform}/${release.releaseRevision}/${host.releaseFile}`;
    const releaseArchive = await getVerifiedDownload(
        context,
        releaseUri,
        releaseSha256,
        `emscripten-${release.version}-${host.key}-${host.releaseFile}`,
    );
    const emsdkDownloads = path.join(emsdkRoot, "downloads");
    await copyDownloadTo(
        releaseArchive,
        path.join(
            emsdkDownloads,
            `${release.releaseRevision}-${host.releaseFile}`,
        ),
    );

    const dependencyRoot = `${EMSCRIPTEN_PACKAGE_ROOT}/deps`;
    const nodeUri = `${dependencyRoot}/${host.nodeFile}`;
    const nodeArchive = await getVerifiedDownload(
        context,
        nodeUri,
        host.nodeSha256,
    );
    await copyDownloadTo(nodeArchive, path.join(emsdkDownloads, host.nodeFile));

    let pythonUri = null;
    if (host.pythonFile) {
        pythonUri = `${dependencyRoot}/${host.pythonFile}`;
        const pythonArchive = await getVerifiedDownload(
            context,
            pythonUri,
            host.pythonSha256,
        );
        await copyDownloadTo(
            pythonArchive,
            path.join(emsdkDownloads, host.pythonFile),
        );
    }

    const emsdkProgram = await context.requirePath(
        path.join(emsdkRoot, "emsdk.py"),
        "emsdk Python entry point",
        "file",
    );
    const installEnvironment = { ...process.env, EMSDK_KEEP_DOWNLOADS: "1" };
    await runCommand(
        bootstrapPython,
        [emsdkProgram, "install", release.version],
        { cwd: emsdkRoot, env: installEnvironment },
    );
    await runCommand(
        bootstrapPython,
        [emsdkProgram, "activate", release.version, "--embedded"],
        { cwd: emsdkRoot, env: installEnvironment },
    );

    const emscriptenRoot = await context.requirePath(
        path.join(emsdkRoot, "upstream", "emscripten"),
        `Emscripten ${release.version}`,
        "directory",
    );
    const versionFile = await context.requirePath(
        path.join(emscriptenRoot, "emscripten-version.txt"),
        "Emscripten version metadata",
        "file",
    );
    const installedVersion = (await readFile(versionFile, "utf8"))
        .trim()
        .replaceAll('"', "");
    if (installedVersion !== release.version) {
        throw new Error(
            `Emscripten at '${emscriptenRoot}' is version '${installedVersion}'; expected '${release.version}'.`,
        );
    }
    await context.requirePath(
        path.join(
            emscriptenRoot,
            process.platform === "win32" ? "emcc.bat" : "emcc",
        ),
        "Emscripten C compiler",
        "file",
    );
    await context.requirePath(
        path.join(
            emscriptenRoot,
            process.platform === "win32" ? "em++.bat" : "em++",
        ),
        "Emscripten C++ compiler",
        "file",
    );
    const config = await context.requirePath(
        path.join(emsdkRoot, ".emscripten"),
        "Emscripten config",
        "file",
    );
    const node = await findEmbeddedExecutable(
        context,
        path.join(emsdkRoot, "node"),
        [process.platform === "win32" ? "node.exe" : "node"],
        "Emscripten Node",
    );
    const nodeVersion = await getProgramVersion(node);
    if (nodeVersion !== "24.19.0") {
        throw new Error(
            `Emscripten ${release.version} uses Node ${nodeVersion}; expected 24.19.0.`,
        );
    }

    let python;
    if (host.pythonFile) {
        python = await findEmbeddedExecutable(
            context,
            path.join(emsdkRoot, "python"),
            process.platform === "win32"
                ? ["python.exe"]
                : ["python3", "python"],
            "Emscripten Python",
        );
    } else {
        python = bootstrapPython;
    }
    const pythonVersion = await getProgramVersion(python, ["--version"]);
    if (host.pythonFile && pythonVersion !== "3.13.3") {
        throw new Error(
            `Emscripten ${release.version} uses Python ${pythonVersion}; expected 3.13.3.`,
        );
    }

    const environment = {
        EMSDK: emsdkRoot,
        EM_CONFIG: config,
        EMSDK_NODE: node,
        EMSDK_PYTHON: python,
        PATH: joinPath([
            path.dirname(python),
            emscriptenRoot,
            path.dirname(node),
            process.env.PATH,
        ]),
    };
    const embuilder = await context.requirePath(
        path.join(emscriptenRoot, "embuilder.py"),
        "Emscripten system library builder",
        "file",
    );
    await runCommand(python, [embuilder, "build", "sysroot", "MINIMAL"], {
        cwd: emscriptenRoot,
        env: { ...process.env, ...environment },
    });
    await runCommand(python, [embuilder, "--pic", "build", "MINIMAL_PIC"], {
        cwd: emscriptenRoot,
        env: { ...process.env, ...environment },
    });

    const libraryRoot = await context.requirePath(
        path.join(
            emscriptenRoot,
            "cache",
            "sysroot",
            "lib",
            "wasm32-emscripten",
        ),
        `Emscripten ${release.version} default system libraries`,
        "directory",
    );
    await context.requirePath(
        path.join(libraryRoot, "pic"),
        `Emscripten ${release.version} PIC system libraries`,
        "directory",
    );

    context.addInstallation({
        id,
        kind: "Emscripten",
        rootPath: emscriptenRoot,
        version: release.version,
        environment,
        sourceUri: releaseUri,
        sha256: releaseSha256,
        revision: `emsdk=${EMSDK_REVISION}; emscripten=${release.releaseRevision}`,
    });
    context.addRuntime({
        id: `node-${release.version}`,
        kind: "Node",
        runtimePath: node,
        version: nodeVersion,
        installationId: id,
        sourceUri: nodeUri,
        sha256: host.nodeSha256,
        revision: "node-v24.19.0",
    });
    context.addRuntime({
        id: `python-${release.version}`,
        kind: "Python",
        runtimePath: python,
        version: pythonVersion,
        installationId: id,
        sourceUri: pythonUri,
        sha256: host.pythonSha256,
        revision: host.pythonFile
            ? "python-3.13.3"
            : `system-python-${pythonVersion}`,
    });
}

async function prepareEmsdkCheckout(context, emsdkRoot, git) {
    const gitDirectory = path.join(emsdkRoot, ".git");
    const hasGitWorktree = await isDirectory(gitDirectory);
    if ((await pathExists(emsdkRoot)) && !hasGitWorktree) {
        console.warn(
            `[emsdk:reset] '${emsdkRoot}' exists without a Git worktree.`,
        );
    }
    if (!hasGitWorktree) {
        await context.resetToolchainDirectory(emsdkRoot);
        await runCommand(git, ["init", emsdkRoot]);
        await runCommand(git, [
            "-C",
            emsdkRoot,
            "remote",
            "add",
            "origin",
            EMSDK_URI,
        ]);
    } else {
        const remotes = await captureCommand(git, ["-C", emsdkRoot, "remote"]);
        if (remotes.stdout.split(/\r?\n/).includes("origin")) {
            await runCommand(git, [
                "-C",
                emsdkRoot,
                "remote",
                "set-url",
                "origin",
                EMSDK_URI,
            ]);
        } else {
            await runCommand(git, [
                "-C",
                emsdkRoot,
                "remote",
                "add",
                "origin",
                EMSDK_URI,
            ]);
        }
    }

    let currentRevision = null;
    if (hasGitWorktree) {
        try {
            const checkout = await captureCommand(git, [
                "-C",
                emsdkRoot,
                "rev-parse",
                "HEAD",
            ]);
            currentRevision = checkout.stdout.trim();
        } catch (error) {
            if (!(error instanceof CommandError)) {
                throw error;
            }
            console.warn(
                `[emsdk:checkout-invalid] root=${emsdkRoot} reason=${error.message}`,
            );
        }
    }

    if (currentRevision !== EMSDK_REVISION) {
        await runCommandWithRetries(git, [
            "-C",
            emsdkRoot,
            "fetch",
            "--depth",
            "1",
            "origin",
            EMSDK_REVISION,
        ]);
    } else {
        console.log(
            `[emsdk:cache-hit] root=${emsdkRoot} revision=${EMSDK_REVISION}`,
        );
    }
    await runCommand(git, [
        "-C",
        emsdkRoot,
        "checkout",
        "--detach",
        "--force",
        EMSDK_REVISION,
    ]);
}

async function prepareWasiSdk(context, version) {
    const platform = hostValue({
        win32: "x86_64-windows",
        darwin: "arm64-macos",
        linux: "x86_64-linux",
    });
    const sha256 = requireHash(
        WASI_HASHES,
        `${version}:${platform}`,
        `WASI SDK ${version}`,
    );
    const uri = `https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-${version}/wasi-sdk-${version}.0-${platform}.tar.gz`;
    const archive = await getVerifiedDownload(context, uri, sha256);
    const clangProbe = path.join(
        "bin",
        process.platform === "win32" ? "clang.exe" : "clang",
    );
    const root = await expandToolArchive(
        context,
        archive,
        path.join(context.toolchainRoot, `wasi-sdk-${version}`),
        clangProbe,
        sha256,
    );
    context.addInstallation({
        id: `wasi-sdk-${version}`,
        kind: "WasiSdk",
        rootPath: root,
        version: `${version}.0`,
        environment: { WASI_SDK_PATH: root },
        sourceUri: uri,
        sha256,
        revision: `wasi-sdk-${version}`,
    });
}

async function prepareWasmtime(context) {
    const version = "45.0.0";
    const host = hostValue({
        win32: {
            platform: "x86_64-windows",
            extension: "zip",
            sha256: "edb9572c6e8ae7c51053af826a8bc85bf205a759c9e83ddb08a941b26e297706",
            probe: "wasmtime.exe",
        },
        darwin: {
            platform: "aarch64-macos",
            extension: "tar.xz",
            sha256: "8c589a1feb6578ddfd76d4ee07bac551d7f3069d6cef9b2ae5e87e630b5198db",
            probe: "wasmtime",
        },
        linux: {
            platform: "x86_64-linux",
            extension: "tar.xz",
            sha256: "9d92e6dc04630f617e0e5d532327a5a917ac4898587e07f4fb7a5fc7fffef760",
            probe: "wasmtime",
        },
    });
    const uri = `https://github.com/bytecodealliance/wasmtime/releases/download/v${version}/wasmtime-v${version}-${host.platform}.${host.extension}`;
    const archive = await getVerifiedDownload(context, uri, host.sha256);
    const root = await expandToolArchive(
        context,
        archive,
        path.join(context.toolchainRoot, `wasmtime-${version}`),
        host.probe,
        host.sha256,
    );
    const runtime = await context.requirePath(
        path.join(root, host.probe),
        `Wasmtime ${version}`,
        "file",
    );
    const actualVersion = await getProgramVersion(runtime);
    if (actualVersion !== version) {
        throw new Error(
            `Wasmtime at '${runtime}' is version '${actualVersion}'; expected '${version}'.`,
        );
    }
    context.addRuntime({
        id: `wasmtime-${version}`,
        kind: "Wasmtime",
        runtimePath: runtime,
        version: actualVersion,
        sourceUri: uri,
        sha256: host.sha256,
        revision: `v${version}`,
    });
}

function emscriptenHost() {
    return hostValue({
        win32: {
            key: "windows",
            releasePlatform: "win",
            releaseFile: "wasm-binaries.zip",
            nodeFile: "node-v24.19.0-win-x64.zip",
            nodeSha256:
                "57f71ab3652e797d84acddc79c81cc9ff1c6ddb2a1974cdb83f00fee9bff4c73",
            pythonFile: "python-3.13.3-0-win-amd64.zip",
            pythonSha256:
                "6fe7a540c6b8b185780467cf7495e884ee62316ec4abb19e1c735b8a77c62465",
        },
        darwin: {
            key: "macos-arm64",
            releasePlatform: "mac",
            releaseFile: "wasm-binaries-arm64.tar.xz",
            nodeFile: "node-v24.19.0-darwin-arm64.tar.gz",
            nodeSha256:
                "8294b7aa9b03997481c06babf1e8b270c859358f27da57a11509afe537ac381d",
            pythonFile: "python-3.13.3-0-macos-arm64.tar.gz",
            pythonSha256:
                "2b0899d7ade9463b0c909ef23aeea27546a4f412637998d817a49718bc2bac19",
        },
        linux: {
            key: "linux",
            releasePlatform: "linux",
            releaseFile: "wasm-binaries.tar.xz",
            nodeFile: "node-v24.19.0-linux-x64.tar.xz",
            nodeSha256:
                "14b342e71204f811bde6153be8e04b62aef63c236fef92b55f9c83154b409647",
            pythonFile: null,
            pythonSha256: null,
        },
    });
}

async function findEmbeddedExecutable(context, root, names, description) {
    const resolvedRoot = await context.requirePath(
        root,
        `${description} root`,
        "directory",
    );
    const expected = new Set(
        names.map((name) =>
            process.platform === "win32" ? name.toLowerCase() : name,
        ),
    );
    const directories = [resolvedRoot];
    const matches = [];
    for (let index = 0; index < directories.length; index += 1) {
        const directory = directories[index];
        const entries = (
            await readdir(directory, { withFileTypes: true })
        ).sort((left, right) => left.name.localeCompare(right.name));
        for (const entry of entries) {
            const candidate = path.join(directory, entry.name);
            if (entry.isDirectory()) {
                directories.push(candidate);
            } else if (
                entry.isFile() &&
                expected.has(
                    process.platform === "win32"
                        ? entry.name.toLowerCase()
                        : entry.name,
                )
            ) {
                matches.push(await realpath(candidate));
            }
        }
    }
    if (matches.length === 0) {
        throw new Error(
            `${description} was not found below '${resolvedRoot}'.`,
        );
    }
    matches.sort((left, right) => left.localeCompare(right));
    return matches[0];
}

function hostValue(values) {
    const value = values[process.platform];
    if (value === undefined) {
        throw new Error(`Unsupported host platform '${process.platform}'.`);
    }
    return value;
}

function requireHash(hashes, key, description) {
    const value = hashes[key];
    if (!value) {
        throw new Error(
            `No ${description} download hash is declared for '${key}'.`,
        );
    }
    return value;
}

function joinPath(parts) {
    return parts
        .filter((part) => typeof part === "string" && part.length > 0)
        .join(path.delimiter);
}

async function pathExists(candidate) {
    try {
        await stat(candidate);
        return true;
    } catch {
        return false;
    }
}

async function isDirectory(candidate) {
    try {
        return (await stat(candidate)).isDirectory();
    } catch {
        return false;
    }
}
