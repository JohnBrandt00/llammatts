# Downloads the NVIDIA CUDA 12 runtime DLLs (cudart + cuBLAS) that llama.cpp's CUDA
# backend needs, into <repo>/data/cuda. Only needed if you don't have a CUDA Toolkit
# installed. ~410 MB download, ~610 MB on disk. Official NVIDIA redistributables.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root "data\cuda"
New-Item -ItemType Directory -Force $outDir | Out-Null

$redist = "https://developer.download.nvidia.com/compute/cuda/redist"
$archives = @(
    "$redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.6.77-archive.zip",
    "$redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.6.4.1-archive.zip"
)
$wanted = @("cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll")

Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($url in $archives) {
    $zipPath = Join-Path $env:TEMP (Split-Path $url -Leaf)
    Write-Host "downloading $(Split-Path $url -Leaf)..."
    Invoke-WebRequest -Uri $url -OutFile $zipPath
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($entry in $zip.Entries) {
            if ($wanted -contains $entry.Name -and $entry.FullName -match "/bin/") {
                $dest = Join-Path $outDir $entry.Name
                Write-Host "  extracting $($entry.Name) ($([math]::Round($entry.Length/1MB)) MB)"
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
            }
        }
    }
    finally { $zip.Dispose() }
    Remove-Item $zipPath -Force
}

Write-Host "done - CUDA runtime in $outDir. Restart LlamaTTS to use the GPU."
