#!/usr/bin/env node

import path from "node:path";
import { prepareBundleToolchains } from "./lib/bundles.mjs";
import { prepareHostToolchains } from "./lib/host-toolchains.mjs";
import { reportSetupFailure, SetupContext } from "./lib/setup-context.mjs";

let context = null;
let profile = "unknown";
try {
    requireSupportedNode();
    const options = parseArguments(process.argv.slice(2));
    if (options.help) {
        printHelp();
    } else {
        profile = options.profile;
        context = new SetupContext(options);
        await context.stage("Initialize", async () => {
            await context.initialize();
        });
        await context.stage("Host toolchains", async () => {
            await prepareHostToolchains(context);
        });
        await context.stage("Cross-platform bundles", async () => {
            await prepareBundleToolchains(context);
        });
        await context.stage("Write environment manifest", async () => {
            await context.writeManifest();
        });
        await context.stage("Build AutoTest", async () => {
            await context.buildAutoTest();
        });
        await context.stage("Export action environment", async () => {
            await context.exportActionEnvironment();
        });
        await context.writeSetupReport("succeeded");
        console.log(
            `[setup:success] profile=${profile} manifest=${context.outputPath} report=${context.setupReportPath}`,
        );
    }
} catch (error) {
    reportSetupFailure(error, profile);
    if (context) {
        try {
            await context.writeSetupReport("failed", error);
        } catch (reportError) {
            console.error(
                `[setup:error] Could not write setup report: ${reportError instanceof Error ? reportError.stack : String(reportError)}`,
            );
        }
    }
    process.exitCode = 1;
}

function parseArguments(args) {
    const values = new Map();
    let help = false;
    for (let index = 0; index < args.length; index += 1) {
        const argument = args[index];
        if (argument === "--help" || argument === "-h") {
            help = true;
            continue;
        }
        if (!argument.startsWith("--")) {
            throw new Error(`Unexpected positional argument '${argument}'.`);
        }
        if (
            ![
                "--profile",
                "--output-path",
                "--toolchain-root",
                "--workspace",
            ].includes(argument)
        ) {
            throw new Error(`Unknown option '${argument}'.`);
        }
        if (values.has(argument)) {
            throw new Error(`Option '${argument}' may only be specified once.`);
        }
        const value = args[index + 1];
        if (!value || value.startsWith("--")) {
            throw new Error(`Option '${argument}' requires a value.`);
        }
        values.set(argument, value);
        index += 1;
    }

    if (help) {
        return { help: true };
    }
    const profileValue = requireOption(values, "--profile");
    const workspace = path.resolve(
        values.get("--workspace") ??
            process.env.GITHUB_WORKSPACE ??
            process.cwd(),
    );
    return {
        help: false,
        profile: profileValue,
        workspace,
        outputPath: path.resolve(
            values.get("--output-path") ??
                path.join(
                    workspace,
                    "build",
                    "toolchain-environments",
                    `${profileValue}.json`,
                ),
        ),
        toolchainRoot: path.resolve(
            values.get("--toolchain-root") ??
                path.join(workspace, "build", "toolchains"),
        ),
    };
}

function requireOption(values, name) {
    const value = values.get(name);
    if (!value) {
        throw new Error(`Required option '${name}' is missing.`);
    }
    return value;
}

function requireSupportedNode() {
    const major = Number.parseInt(process.versions.node.split(".")[0], 10);
    if (!Number.isInteger(major) || major < 20) {
        throw new Error(
            `The toolchain setup requires Node.js 20 or newer; current version is ${process.version}.`,
        );
    }
}

function printHelp() {
    console.log(`Usage: node prepare-environment.mjs --profile <name> [options]

Options:
  --profile <name>         windows-vs2022, windows-vs2026, ubuntu-24.04, or macos-15-arm64
  --workspace <path>       Repository root; defaults to GITHUB_WORKSPACE or the current directory
  --output-path <path>     Environment manifest path
  --toolchain-root <path>  Download and installation root
  --help                    Show this help`);
}
