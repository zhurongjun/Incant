import path from "node:path";
import {
    captureCommand,
    compareVersions,
    getProgramVersion,
    requireCommand,
    resolveCompiler,
    runCommand,
    runCommandWithRetries,
    versionMajor,
} from "./process.mjs";

export async function prepareHostToolchains(context) {
    switch (context.profile) {
        case "windows-vs2022":
        case "windows-vs2026":
            await prepareWindowsEnvironment(context);
            break;
        case "ubuntu-24.04":
            await prepareUbuntuEnvironment(context);
            break;
        case "macos-15-arm64":
            await prepareMacEnvironment(context);
            break;
        default:
            throw new Error(
                `No host toolchain setup is defined for '${context.profile}'.`,
            );
    }
}

async function prepareWindowsEnvironment(context) {
    if (context.profile === "windows-vs2022") {
        await context.stage("Visual Studio 2022 inventory", async () => {
            await addVisualStudio(context, "vs2022", 17);
        });
        for (const version of [
            "10.0.17763.0",
            "10.0.19041.0",
            "10.0.22621.0",
            "10.0.26100.0",
        ]) {
            await context.stage(
                `Windows SDK ${version} inventory`,
                async () => {
                    await addWindowsSdk(context, version);
                },
            );
        }
    } else {
        await context.stage("Visual Studio 2026 inventory", async () => {
            await addVisualStudio(context, "vs2026", 18);
        });
        await context.stage("Windows SDK 10.0.26100.0 inventory", async () => {
            await addWindowsSdk(context, "10.0.26100.0");
        });
    }

    await context.stage("LLVM 20 inventory", async () => {
        const llvm = await resolveCompiler(
            [
                "clang-20.exe",
                "clang.exe",
                "C:\\Program Files\\LLVM\\bin\\clang.exe",
                "C:\\Program Files\\LLVM-20\\bin\\clang.exe",
            ],
            20,
            "LLVM",
        );
        const clangDirectory = path.dirname(llvm.path);
        const clangxx = await context.requirePath(
            path.join(clangDirectory, "clang++.exe"),
            "clang++ 20",
            "file",
        );
        const clangxxVersion = await getProgramVersion(clangxx);
        requireMajorVersion(clangxx, clangxxVersion, 20);
        context.addInstallation({
            id: "llvm-20",
            kind: "Llvm",
            rootPath: llvm.path,
            version: llvm.version,
            environment: {
                CC: llvm.path,
                CXX: clangxx,
                PATH: prependPath(clangDirectory),
            },
        });
    });
}

async function prepareUbuntuEnvironment(context) {
    await context.stage("Ubuntu compiler packages", async () => {
        const sudo = await requireCommand(["sudo"], "sudo");
        const environment = {
            ...process.env,
            DEBIAN_FRONTEND: "noninteractive",
        };
        await runCommandWithRetries(sudo, ["apt-get", "update"], {
            env: environment,
        });
        const packages = [
            "gcc-12",
            "g++-12",
            "gcc-12-multilib",
            "g++-12-multilib",
            "gcc-13",
            "g++-13",
            "gcc-13-multilib",
            "g++-13-multilib",
            "gcc-14",
            "g++-14",
            "gcc-14-multilib",
            "g++-14-multilib",
            "clang-16",
            "llvm-16",
            "lld-16",
            "clang-17",
            "llvm-17",
            "lld-17",
            "clang-18",
            "llvm-18",
            "lld-18",
        ];
        await runCommand(
            sudo,
            [
                "apt-get",
                "install",
                "--yes",
                "--no-install-recommends",
                ...packages,
            ],
            { env: environment },
        );
    });

    for (const major of [12, 13, 14]) {
        await context.stage(`GCC ${major} inventory`, async () => {
            await addCompilerInstallation(
                context,
                `gcc-${major}`,
                "Gnu",
                [`gcc-${major}`],
                major,
                [`g++-${major}`],
            );
        });
    }
    for (const major of [16, 17, 18]) {
        await context.stage(`Clang ${major} inventory`, async () => {
            await addCompilerInstallation(
                context,
                `clang-${major}`,
                "Llvm",
                [`clang-${major}`],
                major,
                [`clang++-${major}`],
            );
        });
    }
}

async function prepareMacEnvironment(context) {
    await context.stage("Xcode 16.4 inventory", async () => {
        await addXcode(context, "xcode-16.4", "Xcode_16.4");
    });
    await context.stage("Xcode 26.3 inventory", async () => {
        await addXcode(context, "xcode-26.3", "Xcode_26.3");
    });
    await context.stage("GCC 14 inventory", async () => {
        await addCompilerInstallation(
            context,
            "gcc-14",
            "Gnu",
            ["/opt/homebrew/bin/gcc-14", "gcc-14"],
            14,
            ["/opt/homebrew/bin/g++-14", "g++-14"],
        );
    });
    await context.stage("Homebrew LLVM 18 inventory", async () => {
        const llvm = await resolveCompiler(
            [
                "/opt/homebrew/opt/llvm@18/bin/clang",
                "/opt/homebrew/opt/llvm/bin/clang",
                "clang-18",
            ],
            18,
            "Homebrew LLVM",
        );
        const clangDirectory = path.dirname(llvm.path);
        const clangxx = await context.requirePath(
            path.join(clangDirectory, "clang++"),
            "Homebrew clang++ 18",
            "file",
        );
        const clangxxVersion = await getProgramVersion(clangxx);
        requireMajorVersion(clangxx, clangxxVersion, 18);
        context.addInstallation({
            id: "llvm-18",
            kind: "Llvm",
            rootPath: llvm.path,
            version: llvm.version,
            environment: {
                CC: llvm.path,
                CXX: clangxx,
                PATH: prependPath(clangDirectory),
            },
        });
    });
}

async function addVisualStudio(context, id, productMajor) {
    const programFilesX86 = process.env["ProgramFiles(x86)"];
    if (!programFilesX86) {
        throw new Error(
            "The ProgramFiles(x86) environment variable is missing.",
        );
    }
    const vswhere = await context.requirePath(
        path.join(
            programFilesX86,
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe",
        ),
        "vswhere",
        "file",
    );
    const result = await captureCommand(vswhere, [
        "-all",
        "-prerelease",
        "-products",
        "*",
        "-format",
        "json",
        "-utf8",
    ]);
    let instances;
    try {
        instances = JSON.parse(result.stdout.replace(/^\uFEFF/, ""));
    } catch (error) {
        throw new Error(
            `vswhere returned invalid JSON: ${result.stdout.slice(0, 2_000)}`,
            {
                cause: error,
            },
        );
    }
    if (!Array.isArray(instances)) {
        throw new Error(
            "vswhere did not return an array of Visual Studio instances.",
        );
    }

    const instance = instances
        .filter(
            (candidate) =>
                versionMajor(candidate.installationVersion) === productMajor,
        )
        .sort((left, right) =>
            compareVersions(
                right.installationVersion,
                left.installationVersion,
            ),
        )[0];
    if (!instance?.installationPath || !instance?.installationVersion) {
        const discovered = instances
            .map(
                (candidate) =>
                    `${candidate.installationPath ?? "unknown"} (${candidate.installationVersion ?? "unknown"})`,
            )
            .join("; ");
        throw new Error(
            `Visual Studio product major ${productMajor} was not found. Discovered: ${discovered || "none"}.`,
        );
    }

    const root = await context.requirePath(
        instance.installationPath,
        `Visual Studio ${productMajor}`,
        "directory",
    );
    await context.requirePath(
        path.join(root, "VC", "Tools", "MSVC"),
        "MSVC toolsets",
        "directory",
    );
    context.addInstallation({
        id,
        kind: "VisualStudio",
        rootPath: root,
        version: instance.installationVersion,
    });
}

async function addWindowsSdk(context, version) {
    const programFilesX86 = process.env["ProgramFiles(x86)"];
    if (!programFilesX86) {
        throw new Error(
            "The ProgramFiles(x86) environment variable is missing.",
        );
    }
    const kitRoot = path.join(programFilesX86, "Windows Kits", "10");
    const includeRoot = await context.requirePath(
        path.join(kitRoot, "Include", version),
        `Windows SDK ${version} include root`,
        "directory",
    );
    await context.requirePath(
        path.join(kitRoot, "Lib", version),
        `Windows SDK ${version} library root`,
        "directory",
    );
    context.addInstallation({
        id: `windows-sdk-${version.split(".")[2]}`,
        kind: "WindowsSdk",
        rootPath: includeRoot,
        version,
    });
}

async function addCompilerInstallation(
    context,
    id,
    kind,
    cCandidates,
    major,
    cxxCandidates,
) {
    const compiler = await resolveCompiler(cCandidates, major, id);
    const cxxCompiler = await resolveCompiler(
        cxxCandidates,
        major,
        `${id} C++ compiler`,
    );
    context.addInstallation({
        id,
        kind,
        rootPath: compiler.path,
        version: compiler.version,
        environment: { CC: compiler.path, CXX: cxxCompiler.path },
    });
}

async function addXcode(context, id, applicationName) {
    const developerRoot = await context.requirePath(
        `/Applications/${applicationName}.app/Contents/Developer`,
        applicationName,
        "directory",
    );
    const xcodebuild = await context.requirePath(
        "/usr/bin/xcodebuild",
        "xcodebuild",
        "file",
    );
    const result = await captureCommand(xcodebuild, ["-version"], {
        env: { ...process.env, DEVELOPER_DIR: developerRoot },
    });
    const match = result.stdout.match(
        /^Xcode\s+(?<version>\d+(?:\.\d+){1,2})/m,
    );
    if (!match?.groups?.version) {
        throw new Error(
            `Could not parse the Xcode version for '${developerRoot}': ${result.stdout.trim()}`,
        );
    }
    context.addInstallation({
        id,
        kind: "Xcode",
        rootPath: developerRoot,
        version: match.groups.version,
        environment: { DEVELOPER_DIR: developerRoot },
    });
}

function requireMajorVersion(executable, version, expectedMajor) {
    if (versionMajor(version) !== expectedMajor) {
        throw new Error(
            `'${executable}' reports version ${version}; expected major version ${expectedMajor}.`,
        );
    }
}

function prependPath(directory) {
    return process.env.PATH
        ? `${directory}${path.delimiter}${process.env.PATH}`
        : directory;
}
