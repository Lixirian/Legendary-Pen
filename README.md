# Legendary Pen

Annotation d'écran pour Windows : dessinez par-dessus n'importe quelle application, à la souris ou au stylet.

## Installation

Téléchargez `LegendaryPen-Setup-x.y.z.exe` depuis la [dernière version](https://github.com/Lixirian/Legendary-Pen/releases/latest), puis lancez-le. Aucun droit administrateur n'est nécessaire.

L'installeur n'étant pas signé par un certificat commercial, Windows SmartScreen peut afficher un avertissement au premier lancement.

## Utilisation

| Action | Commande |
| --- | --- |
| Basculer entre Souris et Stylo | `Ctrl+Maj+Espace` |
| Revenir au mode Souris | `Échap` |
| Annuler / rétablir | `Ctrl+Z` / `Ctrl+Y` |
| Modifier une couleur rapide | Clic droit sur la couleur |
| Mettre la barre à la verticale ou à l'horizontale | Bouton de rotation de la barre |
| Attacher la barre à un bord | La déposer près du bord ; sans clic, elle se replie en une icône, qu'un clic redéplie |
| Stylo / surligneur / gomme | `P` / `H` / `E` |
| Ligne / flèche / rectangle / ellipse | `L` / `A` / `R` / `O` (`Maj` : 45°, carré, cercle) |
| Texte | `T` (`Échap` ou clic ailleurs pour valider) |
| Tableau blanc / tableau noir | `W` / `B` |
| Encre évanescente | `F` |
| Masquer les annotations | `V` |
| Tout effacer | `Suppr` |
| Changer la taille | Molette |

Seul `Ctrl+Maj+Espace` reste réservé en permanence. Les autres touches ne le sont qu'en mode Dessin, et sont libérées pendant la saisie d'un texte.

## Mises à jour

L'application vérifie la présence d'une nouvelle version au lancement, puis toutes les 10 minutes. Au lancement, elle propose de l'installer ; ensuite, une pastille apparaît dans la barre d'outils.

Chaque installeur est vérifié avant son lancement : il doit correspondre à l'empreinte SHA-256 du manifeste, lui-même signé par la clé de publication (RSA 3072).

## Développement

Prérequis : SDK .NET 10, Inno Setup 6, git et GitHub CLI.

```powershell
dotnet test
dotnet run --project src/LegendaryPen.App
```

### Publier une version

```powershell
.\build-release.ps1 -Notes "Ce qui change", "Autre changement"
```

Cette commande augmente la version, lance les tests, construit et signe l'installeur, pousse le code et crée la release GitHub. Un simple `git push` ne publie pas de mise à jour.

Options : `-Bump minor`, `-Bump major` et `-NoPublish` (installeur local uniquement).

Le chemin de la clé de signature se trouve dans `signing-key.local.txt`, un fichier local exclu du dépôt. La clé doit être accessible pour publier et ne doit pas être partagée. Elle ne fonctionne que dans la session Windows qui l'a créée : `-ExportSigningKey` en crée une sauvegarde protégée par un mot de passe, et `-ImportSigningKey` la restaure sur un nouveau Windows ou un autre PC. Si elle est perdue, les applications installées refuseront les nouvelles versions et devront être réinstallées.

## Licence

Tous droits réservés.
