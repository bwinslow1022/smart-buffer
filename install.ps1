# grabs the latest release and runs it
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$url = 'https://github.com/bwinslow1022/smart-buffer/releases/latest/download/SmartBuffer-Setup.exe'
$dir = Join-Path $env:TEMP ("smartbuffer-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $dir | Out-Null

try {
    $exe = Join-Path $dir 'SmartBuffer-Setup.exe'
    Invoke-WebRequest -Uri $url -OutFile $exe -UseBasicParsing
    Start-Process -FilePath $exe -Wait
}
finally {
    Remove-Item -Path $dir -Recurse -Force -ErrorAction SilentlyContinue
}
