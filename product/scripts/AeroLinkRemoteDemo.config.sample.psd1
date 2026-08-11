@{
    # Per-user AeroLink remote-demo configuration.
    # Copy this file to %LOCALAPPDATA%\AeroLink\RemoteDemo\remote-demo.config.psd1
    # and replace the placeholder paths below. This file must contain NO secret
    # values: the ngrok authtoken stays in ngrok's own configuration and the
    # Basic Auth password stays in the ngrok Vault. Only NAMES of Vault secrets
    # may be listed here.
    NgrokExecutable   = 'C:\path\to\ngrok.exe'
    PublicUrl         = 'https://your-endpoint.ngrok-free.dev'
    TrafficPolicyPath = 'C:\path\to\traffic-policy.yml'
    Upstream          = 'http://127.0.0.1:5080'
    LocalApiBaseUri   = 'http://127.0.0.1:5080'
    AeroLinkRoot      = 'C:\path\to\requirements-management-tool'
    # Optional. Defaults are shown and are the expected non-secret names.
    VaultName           = 'aerolink-demo'
    BasicAuthSecretName = 'basic-auth-password'
}
