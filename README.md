# USD-Calc

A modern Windows application built for currency calculations. 

Because this is a debug build, the app package (`.msix`) is signed with a local self-signed development certificate. Windows will block the installation by default with error `0x800B0109`. Follow the instructions below to trust the certificate and install the app.

## 🚀 Installation (Debug Build)

To install the `.msix` package without third-party certificate authority validation, you need to import the embedded certificate into your machine's **Trusted People** store.

### Automated Installation via PowerShell

1. Open **PowerShell** or **Windows Terminal** as **Administrator** (Right-click Start ➔ Terminal (Admin)).
2. Copy and run the following script block:

```powershell
# 1. Extract the self-signed certificate from the MSIX package
cert = (Get-AuthenticodeSignature -FilePath "HOME\Downloads\USD-Calc_1.0.0.0_x64_Debug.msix").SignerCertificate

# 2. Export the certificate to a temporary file
Export-Certificate -Cert cert -FilePath "env:TEMP\usd-calc-dev.cer"

# 3. Import the certificate into the Local Machine's Trusted People store
Import-Certificate -CertStoreLocation Cert:\LocalMachine\TrustedPeople -FilePath "\$env:TEMP\usd-calc-dev.cer"

# 4. Install the MSIX package
Add-AppxPackage -Path "\$HOME\Downloads\USD-Calc_1.0.0.0_x64_Debug.msix"

# 5. Clean up temporary certificate file
Remove-Item "\$env:TEMP\usd-calc-dev.cer"
```

> 💡 **Note:** The script above assumes the `.msix` file is located in your **Downloads** folder (`$HOME\Downloads\`). If you moved the file, update the file paths on lines 2 and 11 before running.

## 🛠️ Troubleshooting

### Error: "Deployment failed with HRESULT: 0x800B0109"
This means the certificate import step was skipped or failed. Ensure you ran the PowerShell window as an **Administrator**. Windows requires elevated privileges to modify the `LocalMachine` certificate store.

### Developer Mode Requirement
If the installation still fails after importing the certificate, you may need to enable Developer Mode on your machine:
1. Open Windows **Settings** (`Win + I`).
2. Navigate to **Privacy & security** ➔ **For developers**.
3. Toggle **Developer Mode** to **On**.
