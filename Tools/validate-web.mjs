import fs from "node:fs";
import vm from "node:vm";

const pages = ["app", "images", "setup"];

for (const page of pages) {
    const sourcePath = `Html/${page}.html`;
    const minifiedPath = `Html/${page}.min.html`;
    const source = fs.readFileSync(sourcePath, "utf8");
    const minified = fs.readFileSync(minifiedPath, "utf8");
    const sourceIds = [...source.matchAll(/\bid=["']([^"']+)["']/g)].map(match => match[1]);
    const minifiedIds = new Set([...minified.matchAll(/\bid=(?:["']([^"']+)["']|([^\s>]+))/g)]
        .map(match => match[1] ?? match[2]));
    const missingIds = sourceIds.filter(id => !minifiedIds.has(id));

    if (missingIds.length > 0) {
        throw new Error(`${minifiedPath} is missing IDs: ${missingIds.join(", ")}`);
    }

    let inlineScriptCount = 0;
    for (const match of minified.matchAll(/<script\b(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi)) {
        inlineScriptCount += 1;
        new vm.Script(match[1], { filename: `${minifiedPath}#inline-${inlineScriptCount}` });
    }

    console.log(`${minifiedPath}: ${sourceIds.length} IDs and ${inlineScriptCount} inline scripts validated`);
}

for (const asset of ["index.min.css", "animations.min.js"]) {
    if (!fs.existsSync(`Html/${asset}`) || fs.statSync(`Html/${asset}`).size === 0) {
        throw new Error(`Html/${asset} is missing or empty.`);
    }
}
