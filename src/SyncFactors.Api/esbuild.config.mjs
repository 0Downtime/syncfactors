import * as esbuild from "esbuild";

const watch = process.argv.includes("--watch");

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
  sourcemap: true,
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
