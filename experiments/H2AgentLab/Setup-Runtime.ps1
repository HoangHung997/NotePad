param(
    [Parameter(Mandatory=$true)][string]$SourcePython,
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'H2AgentLab/runtime/python')
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourcePython)
if (-not (Test-Path -LiteralPath (Join-Path $source 'python.exe')) -or -not (Test-Path -LiteralPath (Join-Path $source 'python312.dll')) -or -not (Test-Path -LiteralPath (Join-Path $source 'Lib/encodings'))) { throw 'Select a CPython 3.12 folder, not a virtual environment.' }
$target = [IO.Path]::GetFullPath($Destination)
if ($target -eq $source -or $target.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $source.StartsWith($target.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source and destination must be separate, non-nested folders.' }
if (Test-Path -LiteralPath (Join-Path $target '.ready')) { Write-Output "Runtime already prepared: $target"; exit 0 }
New-Item -ItemType Directory -Path $target -Force | Out-Null
Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match '^(python.*\.(exe|dll)|vcruntime.*\.dll|LICENSE.txt)$' } | Copy-Item -Destination $target
Copy-Item -LiteralPath (Join-Path $source 'DLLs') -Destination $target -Recurse -Force
$lib = Join-Path $target 'Lib'
New-Item -ItemType Directory -Path $lib -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $source 'Lib') | Where-Object { $_.Name -notin @('site-packages', 'test', '__pycache__', 'idlelib', 'tkinter', 'turtledemo') } | Copy-Item -Destination $lib -Recurse -Force
$packages = Join-Path $lib 'site-packages'
New-Item -ItemType Directory -Path $packages -Force | Out-Null
$names = @('openpyxl','et_xmlfile','docx','python_docx','lxml','typing_extensions','PIL','pillow','pypdf','pypdfium2','reportlab','charset_normalizer')
Get-ChildItem -LiteralPath (Join-Path $source 'Lib/site-packages') | Where-Object {
    $n = $_.Name
    @($names | Where-Object { $n -eq $_ -or $n -eq "$_.py" -or $n -like "$_-*.dist-info" -or ($_ -eq 'pypdfium2' -and $n -like 'pypdfium2_*') }).Count -gt 0
} | Copy-Item -Destination $packages -Recurse -Force
& (Join-Path $target 'python.exe') -I -B -c 'import openpyxl,docx,lxml,pypdf,PIL,pypdfium2,reportlab,struct; assert struct.calcsize("P")==8; print("Office libraries ready (64-bit)")'
if ($LASTEXITCODE -ne 0) { throw 'Runtime validation failed; not marking ready.' }
New-Item -ItemType File -Path (Join-Path $target '.ready') -Force | Out-Null
Write-Output "Prepared private runtime: $target"
