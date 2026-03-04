$scanUrl = "https://av-poc-clam-ntg-b3gqfebzesasczbq.uksouth-01.azurewebsites.net/scan"
$eicarUrl = "https://secure.eicar.org/eicar.com.txt"

# Download file into memory
$response = Invoke-WebRequest -Uri $eicarUrl
$bytes = $response.Content

# Convert string to byte array
$data = [System.Text.Encoding]::ASCII.GetBytes($bytes)

# Create in-memory stream
$stream = New-Object System.IO.MemoryStream(,$data)

# Build multipart form data
$content = New-Object System.Net.Http.MultipartFormDataContent
$fileContent = New-Object System.Net.Http.StreamContent($stream)
$fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("application/octet-stream")

$content.Add($fileContent, "file", "eicar.txt")

# Send request
$client = New-Object System.Net.Http.HttpClient
$result = $client.PostAsync($scanUrl, $content).Result
$body = $result.Content.ReadAsStringAsync().Result

$body