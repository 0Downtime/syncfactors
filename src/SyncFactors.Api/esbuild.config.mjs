import * as esbuild from "esbuild";

const watch = process.argv.includes("--watch");
const sourcemap = watch || process.argv.includes("--sourcemap");

const buildOptions = {
  entryPoints: {
    site: "./frontend/site.entry.js",
    dashboard: "./frontend/dashboard.entry.js"
  },
  bundle: true,
  format: "iife",
  target: ["es2022"],
  outdir: "./wwwroot/dist",
  minify: true,
  sourcemap,
  logLevel: "info",
  legalComments: "none"
};

if (watch) {
  const context = await esbuild.context(buildOptions);
  await context.watch();
  console.log("Watching frontend bundles...");
} else {
  await esbuild.build(buildOptions);
}
