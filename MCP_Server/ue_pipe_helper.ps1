param(
    [string]$PipeName = "unityexplorer_bridge",
    [int]$TimeoutMs = 5000
)

$ErrorActionPreference = "Stop"

try {
    $requestJson = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($requestJson)) {
        throw "Nenhum JSON de request foi enviado para o helper."
    }

    $client = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None
    )

    try {
        $client.Connect($TimeoutMs)

        $writer = [System.IO.StreamWriter]::new($client, [System.Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, [System.Text.UTF8Encoding]::new($false), $false, 4096, $true)

        try {
            $writer.WriteLine($requestJson)
            $responseJson = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($responseJson)) {
                throw "O bridge retornou resposta vazia."
            }

            [Console]::Out.Write($responseJson)
        }
        finally {
            $reader.Dispose()
            $writer.Dispose()
        }
    }
    finally {
        $client.Dispose()
    }
}
catch {
    $payload = @{
        ok = $false
        error = "pipe_request_failed"
        details = $_.Exception.Message
        pipeName = $PipeName
    } | ConvertTo-Json -Compress -Depth 8

    [Console]::Out.Write($payload)
    exit 1
}
