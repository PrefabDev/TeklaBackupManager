# Contributing / Contribuer

This project is distributed under MIT. Keep changes focused and include reproducible tests using fictitious files only. Never attach real settings, model files, credentials, internal host names or unredacted logs.

Windows with .NET 10 SDK:

```powershell
dotnet build TeklaBackupManager.csproj -c Release
dotnet run --project tests/FunctionalTests.csproj -c Release
```

Tests use temporary directories, isolated settings and real worker processes. Preserve both languages and backup modes. Any change to lease or deletion behavior needs tests for refusals without modifications, concurrent processes, failures and recovery. Claims about actual Tekla models or remote shares require authorized representative tests; fictitious-data tests are not enough.

Les contributions doivent conserver les protections de concurrence et de suppression. Utiliser uniquement des fichiers fictifs ; vérifier les limites du scénario testé. Signaler les vulnérabilités selon SECURITY.md.
