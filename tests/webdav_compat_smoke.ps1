param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$Username,

    [Parameter(Mandatory = $true)]
    [string]$Password,

    [switch]$AllowHttpForLocal
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Net.Http

$uri = [System.Uri]::new($BaseUrl)
$isLocalHttp = $AllowHttpForLocal -and
    $uri.Scheme -eq 'http' -and
    ($uri.Host -eq '127.0.0.1' -or $uri.Host -eq 'localhost' -or $uri.Host -eq '::1')

if ($uri.Scheme -ne 'https' -and -not $isLocalHttp) {
    throw 'BaseUrl must be an HTTPS WebDAV folder URL. Use -AllowHttpForLocal only for loopback tests.'
}

$BaseUrl = $BaseUrl.TrimEnd('/')
$client = [System.Net.Http.HttpClient]::new(
    [System.Net.Http.HttpClientHandler]@{
        Credentials = [System.Net.NetworkCredential]::new($Username, $Password)
        PreAuthenticate = $true
        AllowAutoRedirect = $true
    })
$client.Timeout = [TimeSpan]::FromSeconds(30)
$basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${Username}:${Password}"))
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Basic', $basic)

function Join-DavUrl {
    param([string]$Base, [string]$Path)
    if ([string]::IsNullOrEmpty($Path)) {
        return "$Base/"
    }
    $trailingSlash = $Path.EndsWith('/')
    $segments = $Path.Split('/') | Where-Object { $_.Length -gt 0 } | ForEach-Object {
        [System.Uri]::EscapeDataString($_)
    }
    $joined = [string]::Join('/', $segments)
    if ($trailingSlash) {
        return "$Base/$joined/"
    }
    return "$Base/$joined"
}

function Invoke-Dav {
    param(
        [string]$Method,
        [string]$Path,
        [string]$Body = '',
        [hashtable]$Headers = @{},
        [string]$ContentType = 'text/plain'
    )

    $uri = Join-DavUrl $BaseUrl $Path
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $uri)
    foreach ($key in $Headers.Keys) {
        [void]$req.Headers.TryAddWithoutValidation($key, [string]$Headers[$key])
    }
    if ($Body.Length -gt 0 -or $Method -in @('PUT', 'PROPFIND')) {
        $req.Content = [System.Net.Http.StringContent]::new($Body, [System.Text.Encoding]::UTF8, $ContentType)
    }
    try {
        return $client.SendAsync($req).GetAwaiter().GetResult()
    }
    finally {
        $req.Dispose()
    }
}

function Assert-Status {
    param([System.Net.Http.HttpResponseMessage]$Response, [int[]]$Allowed, [string]$Step)
    $code = [int]$Response.StatusCode
    if ($Allowed -notcontains $code) {
        $body = $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        throw "$Step failed: HTTP $code $($Response.ReasonPhrase) $body"
    }
}

$root = 'cloudredirect-compat-' + [Guid]::NewGuid().ToString('N')
$unicodeWord = ([string][char]0x4E2D) + ([string][char]0x6587)
$unicodeFileName = $unicodeWord + ' ' + ([string][char]0x6587) + ([string][char]0x4EF6) + '.txt'
$unicodePayload = "hello $unicodeWord"
$unicodeFile = "$root/$unicodeFileName"
$unicodeEncodedNeedle = [System.Uri]::EscapeDataString($unicodeWord)
$emptyDir = "$root/empty-dir"
$nestedDir = "$root/nested"
$nestedFile = "$nestedDir/deep.txt"
$conflictA = "$root/conflict.txt"

try {
    Assert-Status (Invoke-Dav MKCOL $root) @(201, 405) 'MKCOL root'
    Assert-Status (Invoke-Dav MKCOL $emptyDir) @(201, 405) 'MKCOL empty dir'
    Assert-Status (Invoke-Dav MKCOL $nestedDir) @(201, 405) 'MKCOL nested dir'

    Assert-Status (Invoke-Dav PUT $unicodeFile $unicodePayload) @(200, 201, 204) 'PUT unicode file'
    Assert-Status (Invoke-Dav PUT $nestedFile 'deep') @(200, 201, 204) 'PUT nested file'
    Assert-Status (Invoke-Dav PUT $conflictA 'v1') @(200, 201, 204) 'PUT conflict v1'
    Assert-Status (Invoke-Dav PUT $conflictA 'v2') @(200, 201, 204) 'PUT conflict overwrite'

    $get = Invoke-Dav GET $unicodeFile
    Assert-Status $get @(200) 'GET unicode file'
    $content = $get.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ($content -ne $unicodePayload) {
        throw "GET unicode file returned unexpected content: $content"
    }

    $propfindBody = '<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/></d:prop></d:propfind>'
    $list = Invoke-Dav PROPFIND "$root/" $propfindBody @{ Depth = '1' } 'application/xml'
    Assert-Status $list @(207, 200) 'PROPFIND root'
    $xml = $list.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ($xml -notmatch [regex]::Escape($unicodeWord) -and $xml -notmatch [regex]::Escape($unicodeEncodedNeedle)) {
        throw "PROPFIND root did not include the unicode file. Response: $xml"
    }
    if ($xml -notmatch 'empty-dir') {
        throw 'PROPFIND root did not include the empty directory.'
    }
    if ($xml -notmatch 'nested') {
        throw 'PROPFIND root did not include the nested directory.'
    }

    Assert-Status (Invoke-Dav DELETE $unicodeFile) @(200, 202, 204, 404) 'DELETE unicode file'
    Assert-Status (Invoke-Dav DELETE $nestedFile) @(200, 202, 204, 404) 'DELETE nested file'
    Assert-Status (Invoke-Dav DELETE $conflictA) @(200, 202, 204, 404) 'DELETE conflict file'
    Assert-Status (Invoke-Dav DELETE $nestedDir) @(200, 202, 204, 404) 'DELETE nested dir'
    Assert-Status (Invoke-Dav DELETE $emptyDir) @(200, 202, 204, 404) 'DELETE empty dir'
    Assert-Status (Invoke-Dav DELETE $root) @(200, 202, 204, 404) 'DELETE root'

    Write-Output 'WebDAV compatibility smoke test passed.'
}
finally {
    try { [void](Invoke-Dav DELETE $unicodeFile) } catch {}
    try { [void](Invoke-Dav DELETE $nestedFile) } catch {}
    try { [void](Invoke-Dav DELETE $conflictA) } catch {}
    try { [void](Invoke-Dav DELETE $nestedDir) } catch {}
    try { [void](Invoke-Dav DELETE $emptyDir) } catch {}
    try { [void](Invoke-Dav DELETE $root) } catch {}
    $client.Dispose()
}
