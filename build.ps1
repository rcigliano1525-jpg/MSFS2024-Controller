$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fx = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$meta = Join-Path $env:WINDIR "System32\WinMetadata"
New-Item -ItemType Directory -Force -Path (Join-Path $root "bin") | Out-Null

$refs = @(
    (Join-Path $fx "System.dll"),
    (Join-Path $fx "System.Core.dll"),
    (Join-Path $fx "System.Drawing.dll"),
    (Join-Path $fx "System.Windows.Forms.dll"),
    (Join-Path $fx "System.Xml.dll"),
    (Join-Path $fx "System.Runtime.dll"),
    (Join-Path $fx "System.Runtime.WindowsRuntime.dll"),
    (Join-Path $meta "Windows.Foundation.winmd"),
    (Join-Path $meta "Windows.Globalization.winmd"),
    (Join-Path $meta "Windows.Graphics.winmd"),
    (Join-Path $meta "Windows.Media.winmd"),
    (Join-Path $meta "Windows.Storage.winmd"),
    (Join-Path $meta "Windows.System.winmd"),
    (Join-Path $meta "Windows.UI.winmd")
)
$refArgs = $refs | ForEach-Object { "/reference:$_" }
& $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$root\bin\MSFS2024Start.exe" @refArgs "$root\src\MSFS2024Start.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $root\bin\MSFS2024Start.exe"
