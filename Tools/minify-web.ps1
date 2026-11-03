[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

Push-Location $projectRoot
try {
    & npx.cmd --yes esbuild@0.25.11 Html/index.css --minify --target=chrome100 --outfile=Html/index.min.css
    if ($LASTEXITCODE -ne 0) { throw 'CSS minification failed.' }

    & npx.cmd --yes esbuild@0.25.11 Html/animations.js --minify --target=chrome100 --outfile=Html/animations.min.js
    if ($LASTEXITCODE -ne 0) { throw 'JavaScript minification failed.' }

    $htmlOptions = @(
        '--collapse-whitespace',
        '--remove-comments',
        '--remove-redundant-attributes',
        '--remove-script-type-attributes',
        '--remove-style-link-type-attributes',
        '--use-short-doctype',
        '--minify-css', 'true',
        '--minify-js', 'true'
    )

    foreach ($page in 'app', 'images', 'setup') {
        & npx.cmd --yes html-minifier-terser@7.2.0 "Html/$page.html" -o "Html/$page.min.html" @htmlOptions
        if ($LASTEXITCODE -ne 0) { throw "HTML minification failed for $page.html." }
    }

    & node --check Html/animations.min.js
    if ($LASTEXITCODE -ne 0) { throw 'Minified JavaScript validation failed.' }
}
finally {
    Pop-Location
}
