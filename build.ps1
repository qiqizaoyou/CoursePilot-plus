param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '未找到 Windows .NET Framework 4.x 编译器。' }
$output = Join-Path $projectRoot 'dist\v0.6.13'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','System.Security.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Runtime.WindowsRuntime.dll','System.Runtime.InteropServices.WindowsRuntime.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$metadata = Join-Path $env:WINDIR 'System32\WinMetadata'
$references += @('Windows.Foundation.winmd','Windows.Media.winmd','Windows.Graphics.winmd','Windows.Globalization.winmd','Windows.Storage.winmd') | ForEach-Object { '/reference:' + (Join-Path $metadata $_) }
$runtimeFacade = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime') -Recurse -Filter 'System.Runtime.dll' | Select-Object -First 1 -ExpandProperty FullName
if (-not $runtimeFacade) { throw '未找到 Windows Runtime 基础程序集。' }
$references += '/reference:' + $runtimeFacade
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $framework 'WPF') $_) }
$sourceFiles = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/codepage:65001',('/out:' + (Join-Path $output '随课.exe')),('/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest')),('/win32icon:' + (Join-Path $projectRoot 'assets\app.ico')),('/resource:' + (Join-Path $projectRoot 'src\MainWindow.xaml') + ',MainWindow.xaml'),('/resource:' + (Join-Path $projectRoot 'assets\app.ico') + ',app.ico'))
& $compiler @compilerArgs @references @sourceFiles
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\app.config') -Destination (Join-Path $output '随课.exe.config') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot '使用说明.txt') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'COMPATIBILITY.md') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $output -Force
Write-Output ('已生成：' + (Join-Path $output '随课.exe'))
