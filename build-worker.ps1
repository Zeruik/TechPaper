param()
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$py=Join-Path $PSScriptRoot 'build/worker-env/Scripts/python.exe'
if(!(Test-Path $py)){py -3.12 -m venv build/worker-env;if($LASTEXITCODE){throw 'Python 3.12 x64 required'}}
& $py -m pip install --disable-pip-version-check -r worker/requirements.lock
if($LASTEXITCODE){throw 'Worker dependencies failed'}
& $py worker/notices.py
if($LASTEXITCODE){throw 'License collection failed'}
& $py -m PyInstaller --noconfirm --name review-worker --distpath build/dist --workpath build/pyi --specpath build --collect-all rapidocr_onnxruntime --collect-all pypdfium2_raw --collect-all onnxruntime --exclude-module matplotlib --exclude-module tkinter worker/review.py
if($LASTEXITCODE){throw 'Worker build failed'}
