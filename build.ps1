param([string]$Iscc, [switch]$AppOnly, [switch]$Test, [switch]$DesktopTest)
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$csc=Join-Path $framework 'csc.exe'
New-Item -ItemType Directory -Force build,release | Out-Null
$refs=@('/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Web.Extensions.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll')
& $csc /nologo /codepage:65001 /optimize+ /target:winexe /win32manifest:src\app.manifest /win32icon:assets\TechPaper.ico /out:build\TechPaper.exe @refs src\Workspace.cs src\FileReview.cs src\Manuscript.cs src\Services.cs
if($LASTEXITCODE){throw 'Application compilation failed'}
if($Test){
    & $csc /nologo /codepage:65001 /out:build\Regression.exe @refs tests\Regression.cs src\Manuscript.cs src\Services.cs
    if($LASTEXITCODE){throw 'Test compilation failed'}
    & ./build/Regression.exe
    if($LASTEXITCODE){throw 'Regression failed'}
}
if($DesktopTest){
    if(!(Test-Path 'build/dist/review-worker/review-worker.exe')){throw 'Run build-worker.ps1 before -DesktopTest'}
    & $csc /nologo /codepage:65001 /main:DesktopChecks /win32icon:assets\TechPaper.ico /out:build\DesktopChecks.exe @refs tests\DesktopChecks.cs src\*.cs
    if($LASTEXITCODE){throw 'Desktop test compilation failed'}
    & ./build/DesktopChecks.exe
    if($LASTEXITCODE){throw 'Desktop regression failed'}
}
if($AppOnly){return}
& ./build-worker.ps1
if(!$Iscc){$Iscc=(Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source}
if(!$Iscc){
    $install=Get-ItemProperty 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/*','HKLM:/SOFTWARE/WOW6432Node/Microsoft/Windows/CurrentVersion/Uninstall/*' -ErrorAction SilentlyContinue |
        Where-Object {$_.DisplayName -match '^Inno Setup (version )?6' -and $_.InstallLocation} | Select-Object -First 1
    if($install){$Iscc=Join-Path $install.InstallLocation 'ISCC.exe'}
}
if(!$Iscc){throw 'Install Inno Setup 6 or pass -Iscc <path>'}
& $Iscc /Qp installer.iss
if($LASTEXITCODE){throw 'Installer compilation failed'}
$file=Get-Item release/TechPaper-Setup-0.9.0-preview.2.exe
("{0}  {1}" -f (Get-FileHash $file.FullName).Hash.ToLowerInvariant(),$file.Name) | Set-Content release/SHA256SUMS.txt -Encoding ascii
