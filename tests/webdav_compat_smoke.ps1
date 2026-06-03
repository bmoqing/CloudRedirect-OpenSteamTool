param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$Username,

    [Parameter(Mandatory = $true)]
    [string]$Password
)

$ErrorActionPreference = 'Stop'

if (-not $BaseUrl.StartsWith('https://', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'BaseUrl must be an HTTPS WebDAV folder URL.'
}

$BaseUrl = $BaseUrl.TrimEnd('/')
$client = [System.Net.Http.HttpClient]::new(
    [System.Net.Http.HttpClientHandler]@{
        Credentials = [System.Net.NetworkCredential]::new($Username, $Password)
        PreAuthenticate = $true
        AllowAutoRedirect = $true
    })
$client.Timeout = [TimeSpan]::FromSeconds(30)

function Invoke-Dav {
    param(
        [string]$Method,
        [string]$Path,
        [string]$Body = '',
        [hashtable]$Headers = @{},
        [string]$ContentType = 'text/plain'
    )

    $uri = "$BaseUrl/$Path"
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $uri)
    foreach ($key in $Headers.Keys) {
        [void]$req.Headers.TryAddWithoutValidation($key, [string]$Headers[$key])
    }
    if ($Body.Length -gt 0 -or $Method -in @('PUT', 'PROPFIND')) {
        $req.Content = [System.Net.Http.StringContent]::new($Body, [System.Text.Encoding]::UTF8, $ContentType)
    }
    try {
        return $client.Send($req)
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
$unicodeFile = "$root/中文 文件.txt"
$emptyDir = "$root/empty-dir"
$conflictA = "$root/conflict.txt"

try {
    Assert-Status (Invoke-Dav MKCOL $root) @(201, 405) 'MKCOL root'
    Assert-Status (Invoke-Dav MKCOL $emptyDir) @(201, 405) 'MKCOL empty dir'

    Assert-Status (Invoke-Dav PUT $unicodeFile 'hello 中文') @(200, 201, 204) 'PUT unicode file'
    Assert-Status (Invoke-Dav PUT $conflictA 'v1') @(200, 201, 204) 'PUT conflict v1'
    Assert-Status (Invoke-Dav PUT $conflictA 'v2') @(200, 201, 204) 'PUT conflict overwrite'

    $get = Invoke-Dav GET $unicodeFile
    Assert-Status $get @(200) 'GET unicode file'
    $content = $get.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ($content -ne 'hello 中文') {
        throw "GET unicode file returned unexpected content: $content"
    }

    $propfindBody = '<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/></d:prop></d:propfind>'
    $list = Invoke-Dav PROPFIND "$root/" $propfindBody @{ Depth = '1' } 'application/xml'
    Assert-Status $list @(207, 200) 'PROPFIND root'
    $xml = $list.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ($xml -notmatch '中文|%E4%B8%AD%E6%96%87') {
        throw 'PROPFIND root did not include the unicode file.'
    }
    if ($xml -notmatch 'empty-dir') {
        throw 'PROPFIND root did not include the empty directory.'
    }

    Assert-Status (Invoke-Dav DELETE $unicodeFile) @(200, 202, 204, 404) 'DELETE unicode file'
    Assert-Status (Invoke-Dav DELETE $conflictA) @(200, 202, 204, 404) 'DELETE conflict file'
    Assert-Status (Invoke-Dav DELETE $emptyDir) @(200, 202, 204, 404) 'DELETE empty dir'
    Assert-Status (Invoke-Dav DELETE $root) @(200, 202, 204, 404) 'DELETE root'

    Write-Output 'WebDAV compatibility smoke test passed.'
}
finally {
    try { [void](Invoke-Dav DELETE $unicodeFile) } catch {}
    try { [void](Invoke-Dav DELETE $conflictA) } catch {}
    try { [void](Invoke-Dav DELETE $emptyDir) } catch {}
    try { [void](Invoke-Dav DELETE $root) } catch {}
    $client.Dispose()
}
