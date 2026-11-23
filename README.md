# Tekla Backup Manager — Community Edition

[English](README.en.md) · [Télécharger 0.1.0-alpha.2](https://github.com/PrefabDev/TeklaBackupManager/releases/tag/v0.1.0-alpha.2) · [MIT](LICENSE)

Application Windows C# / WPF pour sauvegarder les dossiers de modèles Tekla Structures : DB1 les jours ouvrés, miroir complet le vendredi, rétention et gestion de plusieurs utilisateurs, interface français/anglais.

**0.1.0-alpha.2 est une version communautaire expérimentale, pas une version stable ou certifiée pour la production.** Conserver une sauvegarde indépendante et tester les restaurations avant toute utilisation réelle. Aucun service commercial ou délai de support n’est garanti.

## Installation

1. Télécharger `TeklaBackupManager-0.1.0-alpha.2-win-x64.zip` depuis la prerelease, puis vérifier son empreinte SHA-256 avec `SHA256SUMS.txt`.
2. Extraire le ZIP dans un dossier utilisateur stable ; conserver les fichiers LICENSE et README fournis.
3. Lancer `TeklaBackupManager.exe`. Il est autonome pour Windows x64 et ne nécessite pas l’installation de .NET. Le binaire n’est pas signé ; ne pas désactiver les protections Windows pour l’exécuter.
4. Commencer sur des fichiers fictifs et une **nouvelle racine de sauvegarde dédiée**, jamais sur l’unique copie d’un modèle.

Le chargement du binaire autonome a été testé dans Windows Sandbox sans installation partagée de .NET détectée. Cela ne certifie pas toutes les éditions de Windows ni les fonctions de réseau/restauration.

## Configuration

Les permissions sont celles de la session Windows : lecture des sources, écriture des destinations. Aucun mot de passe n’est demandé ou stocké. Les valeurs initiales de nom et poste proviennent de la session locale et doivent être vérifiées.

Exemples fictifs : `\\WORKSTATION-01\TeklaStructuresModels` et `\\BACKUP-SERVER\TeklaBackups`. Un dossier source peut contenir plusieurs projets. Ajouter les postes, cocher ceux à traiter et définir leurs heures `HH:mm`. Sélectionner un poste puis **Appliquer les horaires au poste sélectionné**, ou modifier les heures du tableau, puis enregistrer.

Les paramètres sont locaux : `%AppData%\TeklaBackupManager\settings.json`. Aucune configuration réelle n’est distribuée. Une installation antérieure utilise le même emplacement : sauvegarder ce fichier avant changement de version. Fermer la fenêtre masque l’application dans la zone de notification ; **Quitter** dans son menu arrête les automatismes. Le démarrage avec la session Windows est optionnel ; ce scénario après reconnexion n’est pas encore qualifié. Certains messages et le menu de notification restent en français.

## Modes de sauvegarde

- **DB1** : copie les fichiers `.db1` du lundi au vendredi, par défaut à 12:15 ; rétention configurable, 30 jours par défaut. L’absence de DB1 refuse la sauvegarde et la purge. Plusieurs exécutions le même jour mettent à jour le même dossier.
- **Complet** : miroir le vendredi, par défaut à 12:30, incluant tous les fichiers. Les fichiers absents de la source sont **supprimés de la destination**. Aucun historique complet ; une source totalement vide est refusée.
- Exécution manuelle, progression, annulation et décalage entre utilisateurs d’un même lot.

```text
[Destination]/BACKUPDB1/[UTILISATEUR]/yyyy-dd-MM
[Destination]/TEKLASTRUCTUREMODEL/[UTILISATEUR]
```

Le format historique est année-jour-mois. La purge ne concerne que les dossiers datés anciens et dont les dates de création/modification dépassent la rétention. Les métadonnées sont séparées des modèles dans `.tekla-backup-manifests` ; un fichier source `manifest.txt` est préservé normalement. Les manifestes ne sont pas soumis à la purge DB1 : prévoir leur archivage séparé.

Les échéances sont contrôlées toutes les 30 secondes tant que l’application et la session restent actives. Les heures dépassées peuvent être rattrapées le même jour, pas les jours manqués. Les clés d’exécution restent en mémoire : redémarrer peut relancer une tâche du jour ; les échecs automatiques ne sont pas réessayés dans le même processus. Les DB1 sont prioritaires sur les miroirs.

## Concurrence, noms et migration

Une seule instance peut utiliser un profil. Une destination est verrouillée pendant chaque sauvegarde : un autre processus est refusé, sans attendre ni fusionner. Le système de fichiers doit faire respecter le partage exclusif et les opérations de verrouillage vérifiées ; sinon l’outil refuse/arrête les écritures. Les handles sont libérés à la sortie ou par Windows après arrêt forcé. Les fichiers de verrou persistent volontairement : **ne pas les supprimer pour forcer l’accès**.

Les noms Windows réservés/interdits, les collisions de casse et les cibles identiques sont refusés, même pour des utilisateurs désactivés ou un lancement planifié partiel. Les identités de dossiers sont résolues avant comparaison ; les chemins ambigus, les jonctions/liens et les racines imbriquées sont refusés.

La racine contient `.tekla-backup-owners.json`, associant chaque utilisateur à une source. Une autre source ne peut pas reprendre ce nom. Ne pas supprimer ce registre. **Les sauvegardes antérieures sans identité vérifiable ne sont pas automatiquement adoptées** : utiliser une nouvelle racine et conserver les anciennes sauvegardes séparément. Aucun renommage ni migration silencieuse.

## Intégrité et restauration

Les fichiers nouveaux/modifiés sont copiés dans des fichiers temporaires puis remplacés par renommage dans le même dossier après vérification du verrou. Une annulation pendant la préparation conserve le fichier précédent. Le miroir compare aussi le contenu par SHA-256 pour détecter les changements de même taille/date. Il ne s’agit pas d’une transaction de modèle complet : une interruption peut laisser une version partiellement mise à jour ou un temporaire orphelin. Un arrêt pendant la suppression peut laisser un miroir partiellement purgé.

Pour restaurer : arrêter les automatismes, fermer le modèle Tekla, préserver son état actuel, copier le modèle sauvegardé dans un **nouvel emplacement**, puis l’ouvrir dans une version compatible et vérifier les données métier avant tout remplacement. Un `.db1` seul n’est pas une sauvegarde complète ; faire valider sa réintégration avec les autres fichiers par l’administrateur Tekla.

**Aucun modèle Tekla représentatif autorisé n’a été disponible pour qualifier une restauration réelle.** Aucun assistant de restauration, snapshot VSS ou coordination avec Tekla n’est implémenté. Les modèles ouverts peuvent changer pendant la copie ; préférer des modèles fermés et vérifier les restaurations indépendamment.

## Qualification et limites

54 tests sur fichiers fictifs, dont plusieurs processus réels : verrous/refus sans altération, arrêt forcé, rétention, DB1, miroir, collisions, annulation, copie de retour et empreintes. Test de démarrage dans Windows Sandbox. Cela ne garantit pas la restaurabilité d’un modèle Tekla.

Les pertes de connexion réelles, les équipements SMB/NAS, plusieurs postes distants, la charge et les horaires sur plusieurs jours ne sont pas qualifiés. Le mécanisme utilise les handles exclusifs Windows/SMB compatibles et échoue si leur contrôle échoue ; ne pas l’utiliser sur un fournisseur dont ces garanties ne sont pas établies. Pas de chiffrement, contrôle d’espace disque, garantie contre un administrateur hostile ou snapshot atomique. L’application et les autres outils doivent respecter les mêmes protections ; un script tiers peut ignorer ce protocole.

Réglages, registre de sources, manifestes et journaux peuvent contenir des identifiants/chemins locaux : ne jamais les publier sans anonymisation. Aucun mécanisme de télémétrie ou d’envoi de données n’a été identifié dans le code applicatif ; les chemins UNC utilisent le réseau Windows.

## Compiler et contribuer

Windows et SDK .NET 10 maintenu. Aucun SDK Tekla requis.

```powershell
dotnet build TeklaBackupManager.csproj -c Release
dotnet run --project tests/FunctionalTests.csproj -c Release
.\publish.ps1
```

La sortie autonome est `artifacts/win-x64/TeklaBackupManager.exe`. Voir [CONTRIBUTING.md](CONTRIBUTING.md) et [SECURITY.md](SECURITY.md).

MIT — Copyright (c) 2026 Benoît Gosselin. Outil communautaire indépendant, non édité, affilié ou approuvé par Trimble ou Tekla. Les marques Tekla Structures et Trimble appartiennent à leurs propriétaires respectifs. Aucun logo tiers n’est distribué.

Deux essais supplémentaires dans le bac à sable ont validé une destination indépendante sur lecteur substitué et le refus d’un chevauchement entre ce lecteur et son chemin réel.
