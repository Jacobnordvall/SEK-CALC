# SEK-CALC / USD-Calc

Ett Windows-program för valutaomvandling.

Eftersom detta är ett debug-bygge är app-paketet (.msix) signerat med ett lokalt självsignerat utvecklarcertifikat. Windows blockerar installationen som standard med felkoden 0x800B0109. Följ instruktionerna nedan för att lita på certifikatet och installera appen automatiskt via PowerShell.

## 🚀 Installation (Debug-bygge)

Detta skript söker automatiskt upp MSIX-filen i din Downloads-mapp (inklusive undermappar som `USD-Calc_1.0.0.0_x64_Debug_Test`), extraherar utvecklarcertifikatet, sparar det till systemets betrodda lista och slutför installationen.

### Automatisk installation via PowerShell

1. Högerklicka på **Start-menyn** och välj **Terminal (Administratör)** eller **Windows PowerShell (Administratör)**.
2. Kopiera och klistra in följande skriptblock i sin helhet och tryck på **Enter**:

```powershell
Get-ChildItem -Path "$env:USERPROFILE\Downloads" -Recurse -Filter "USD-Calc*.msix" -ErrorAction SilentlyContinue | Select-Object -First 1 | ForEach-Object {
    Write-Host "Hittade installerare: $($_.FullName)" -ForegroundColor Green
    
    # Exportera certifikatet direkt från filen till en temporär fil
    $cert = (Get-AuthenticodeSignature -FilePath $_.FullName).SignerCertificate
    if ($null -ne $cert) {
        $tempCert = Join-Path $env:TEMP "usddev.cer"
        Export-Certificate -Cert $cert -FilePath $tempCert | Out-Null
        
        # Importera certifikatet till datorns betrodda personer
        Import-Certificate -CertStoreLocation Cert:\LocalMachine\TrustedPeople -FilePath $tempCert | Out-Null
        Write-Host "Certifikatet har lagts till i Betrodda personer." -ForegroundColor Green
        
        # Rensa temporär fil
        Remove-Item $tempCert -ErrorAction SilentlyContinue
    } else {
        Write-Warning "Kunde inte hitta något giltigt certifikat i MSIX-paketet."
    }
    
    # Installera själva MSIX-paketet
    Add-AppxPackage -Path $_.FullName
    Write-Host "Klart! Appen har installerats utan problem." -ForegroundColor Green
}
