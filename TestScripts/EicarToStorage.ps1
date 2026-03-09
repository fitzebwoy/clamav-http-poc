param(
    [Parameter(Mandatory = $true)]
    [string]$BlobSasUrl,

    [string]$EicarUrl = "https://secure.eicar.org/eicar.com.txt"
)

[Net.ServicePointManager]::SecurityProtocol = `
    [Net.SecurityProtocolType]::Tls12 -bor `
    [Net.SecurityProtocolType]::Tls13

$response = Invoke-WebRequest -Uri $EicarUrl -Method Get -UseBasicParsing

if (-not $response.Content) {
    throw "Downloaded content is empty."
}

# Convert the returned string into bytes
$contentBytes = [System.Text.Encoding]::ASCII.GetBytes($response.Content)

$headers = @{
    "x-ms-blob-type" = "BlockBlob"
    "x-ms-version"   = "2023-11-03"
    "Content-Type"   = "text/plain"
}

Invoke-RestMethod `
    -Uri $BlobSasUrl `
    -Method Put `
    -Headers $headers `
    -Body $contentBytes

Write-Host "Upload completed successfully."
Write-Host ("Bytes uploaded: {0}" -f $contentBytes.Length)