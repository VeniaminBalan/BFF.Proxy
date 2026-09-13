// Stand-in for a real bundler (Vite/webpack/CRA/etc). It just copies src/ to dist/ so this
// sample runs with zero npm dependencies - swap this whole file (and package.json) for your
// actual frontend project.
const fs = require("node:fs");

fs.rmSync("dist", { recursive: true, force: true });
fs.cpSync("src", "dist", { recursive: true });
console.log("Built src/ -> dist/");
