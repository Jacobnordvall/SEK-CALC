# SEK-CALC / USD-Calc

Ett Windows-program för valutaomvandling.

Eftersom detta är ett debug-bygge är app-paketet (.msix) signerat med ett lokalt självsignerat utvecklarcertifikat. Windows blockerar installationen som standard med felkoden 0x800B0109. Följ instruktionerna nedan för att lita på certifikatet och installera appen automatiskt via PowerShell.

## 🚀 Installation (Debug-bygge)

Detta skript söker automatiskt upp MSIX-filen i din Downloads-mapp (inklusive undermappar som `USD-Calc_1.0.0.0_x64_Debug_Test`), extraherar utvecklarcertifikatet, sparar det till systemets betrodda lista och slutför installationen.

### Automatisk installation via PowerShell

1. Verifiera att filen blev nedladdad.
2. Högerklicka på **Start-menyn** och välj **Terminal (Administratör)** eller **Windows PowerShell (Administratör)**.
3. Kopiera och klistra in följande skriptblock i sin helhet och tryck på **Enter**:
4. Ta bort filen ur din downloads mapp.

```powershell
$installer = Get-ChildItem -Path "$env:USERPROFILE\Downloads" -Recurse -Filter "USD-Calc*.msix" -ErrorAction SilentlyContinue | Select-Object -First 1

if ($null -eq $installer) {
    Write-Host "Ingen USD-Calc*.msix-installationsfil hittades i Downloads." -ForegroundColor Red
    Write-Host "Kontrollera att MSIX-filen finns i din Downloads-mapp och försök igen." -ForegroundColor Red

return
}

Write-Host "Hittade installerare: $($installer.FullName)" -ForegroundColor Green

# Exportera certifikatet direkt från filen till en temporär fil
$cert = (Get-AuthenticodeSignature -FilePath $installer.FullName).SignerCertificate

if ($null -ne $cert) {
    $tempCert = Join-Path $env:TEMP "usddev.cer"
    Export-Certificate -Cert $cert -FilePath $tempCert | Out-Null
    
    # Importera certifikatet till datorns betrodda personer
    Import-Certificate -CertStoreLocation Cert:\LocalMachine\TrustedPeople -FilePath $tempCert | Out-Null
    Write-Host "Certifikatet har lagts till i Betrodda personer." -ForegroundColor Green
    
    # Rensa temporär fil
    Remove-Item $tempCert -ErrorAction SilentlyContinue
}
else {
    Write-Warning "Kunde inte hitta något giltigt certifikat i MSIX-paketet."
}

# Installera själva MSIX-paketet
Add-AppxPackage -Path $installer.FullName

Write-Host "Klart! Appen har installerats utan problem." -ForegroundColor Green



