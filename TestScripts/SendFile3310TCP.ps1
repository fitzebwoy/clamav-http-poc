param(
    [Parameter(Mandatory=$true)]
    [string]$FilePath,

    [string]$ClamHost = "20.26.54.127",
    [int]$ClamPort = 3310
)

if (!(Test-Path $FilePath)) {
    Write-Error "File not found: $FilePath"
    exit 1
}

$client = New-Object System.Net.Sockets.TcpClient
$client.Connect($ClamHost, $ClamPort)
$stream = $client.GetStream()

# Send INSTREAM command
$command = [System.Text.Encoding]::ASCII.GetBytes("zINSTREAM`0")
$stream.Write($command, 0, $command.Length)

$bufferSize = 8192
$fileStream = [System.IO.File]::OpenRead($FilePath)
$buffer = New-Object byte[] $bufferSize

while (($bytesRead = $fileStream.Read($buffer, 0, $bufferSize)) -gt 0) {
    $sizeBytes = [System.BitConverter]::GetBytes([System.Net.IPAddress]::HostToNetworkOrder($bytesRead))
    $stream.Write($sizeBytes, 0, 4)
    $stream.Write($buffer, 0, $bytesRead)
}

# Send zero-length chunk to mark EOF
$end = [byte[]](0,0,0,0)
$stream.Write($end, 0, 4)

$fileStream.Close()

# Read response
$responseBuffer = New-Object byte[] 4096
$responseBytes = $stream.Read($responseBuffer, 0, $responseBuffer.Length)
$response = [System.Text.Encoding]::ASCII.GetString($responseBuffer, 0, $responseBytes)

$stream.Close()
$client.Close()

Write-Output "ClamAV Response: $response"