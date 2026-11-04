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
| Afficher ou masquer la deuxième ligne de la barre | Bouton flèche, à côté de « Quitter » ; une pastille rouge signale un réglage actif sur la ligne masquée |
| Attacher la barre à un bord | La déposer près du bord ; sans clic, elle redescend derrière le bord et laisse une petite vague, qu'un clic fait remonter |
| Stylo / surligneur / gomme | `P` / `H` / `E` |
| Ligne / flèche / rectangle / ellipse | `L` / `A` / `R` / `O` (`Maj` : 45°, carré, cercle) |
| Texte | `T` (`Échap` ou clic ailleurs pour valider) |
| Tableau blanc / tableau noir | `W` / `B` |
| Encre évanescente | `F` |
| Masquer les annotations | `V` |
| Tout effacer | `Suppr` |
| Capture d'écran | `C` ou bouton de la barre |
| Afficher ou masquer la barre | `Ctrl+Maj+F9` ou icône près de l'horloge |
| Changer la taille | Molette |

Tous les raccourcis se modifient dans les réglages. Seuls `Ctrl+Maj+Espace` et `Ctrl+Maj+F9` restent réservés en permanence, avec ceux que l'on attribue au mode présentation et à l'enregistrement vidéo. Les autres touches ne le sont qu'en mode Dessin, et sont libérées pendant la saisie d'un texte.

Au relâchement, le surligneur devient un trait droit et se cale sur la ligne de texte située dessous. L'analyse de l'écran reste locale ; rien n'est enregistré ni envoyé. L'option se désactive dans les réglages.

En option (désactivée par défaut), une ligne, un rectangle ou une ellipse tracés au stylo sont remplacés par la forme nette ; un premier Ctrl+Z rend le tracé brut.

## Langues

L'interface est disponible en français et en anglais. Elle suit la langue de Windows, ou le choix fait dans les réglages, onglet Général.

## Mode présentation

Le bouton « Présentation » de la barre ouvre un menu de trois cases indépendantes, que le menu de l'icône près de l'horloge propose aussi directement :

- **Projecteur** : le pointeur éclaire un disque, le reste de l'écran est assombri.
- **Loupe** : un disque grossit la zone sous le pointeur. Elle reste visible dans un partage d'écran, ce que nous avons mesuré dans une capture Windows.Graphics.Capture.
- **Effets de clic** : un anneau part de chaque clic, de la couleur du stylo pour le clic gauche et gris clair pour le clic droit.

Aucun raccourci n'est attribué par défaut ; chacun se choisit dans les réglages. Les effets sont suspendus pendant une capture d'écran. Aucune position ni aucun clic n'est enregistré ou envoyé : le crochet souris ne sert qu'à dessiner l'anneau, et il n'est installé que lorsque les effets de clic sont activés.

## Calques

Chaque fond (transparent, tableau blanc, tableau noir) compte de 1 à 8 calques, communs à tous les écrans. Le bouton « Calques » de la barre ouvre un panneau qui reste ouvert pendant le dessin :

- le calque du haut de la liste est celui du dessus ; un clic sur une ligne le rend actif, et seul le calque actif reçoit le tracé, le texte et la gomme ;
- la case de chaque ligne affiche ou masque le calque, et les flèches le montent ou le descendent. Le calque actif n'est jamais masqué, et le dernier calque visible ne peut pas l'être non plus ;
- « Ajouter » crée un calque au-dessus du calque actif, « Supprimer » retire le calque actif.

Ajouter, supprimer ou déplacer un calque s'annule avec Ctrl+Z, contenu compris ; la visibilité et le calque actif n'entrent pas dans l'historique, et une annulation peut les ramener à leur état d'alors. Annuler agit toujours sur le calque concerné, et « Tout effacer » vide tous les calques en une seule action annulable. Avec « tableau sur un seul écran » (réglage « Afficher le tableau uniquement sur l'écran de la barre d'outils »), le panneau gère les calques du tableau. Aucun raccourci n'est attribué par défaut ; il se choisit dans les réglages.

Quand les annotations sont conservées, les calques le sont aussi : ordre, visibilité et calque actif reviennent au redémarrage, et les anciens fichiers d'annotations restent lisibles. Si un écran branché en cours de route apporte ses propres calques, ils s'ajoutent à la pile sans en retirer aucun, et l'historique d'annulation de ce fond repart de zéro. Revenir à une version antérieure à 0.9 après avoir créé des calques peut faire perdre ces annotations.

## Enregistrement vidéo

Le bouton rond de la barre, ou « Enregistrer l'écran » dans le menu de l'icône près de l'horloge, filme l'écran où se trouve la barre quand on lance l'enregistrement, après un décompte de 3 secondes. La même commande annule le décompte, puis arrête l'enregistrement ; pendant ce temps, le bouton devient rouge et affiche la durée. Aucun raccourci n'est attribué par défaut ; il se choisit dans les réglages.

- **Le cadre jaune :** Windows entoure l'écran filmé d'un cadre jaune tant que la capture dure. Il signale l'enregistrement et reste affiché ; il ne figure pas dans la vidéo.
- **Ce qui apparaît dans la vidéo :** tout ce qui est à l'écran, avec les annotations, les tableaux, le curseur et les effets de présentation. La barre, le panneau des calques, le décompte et les messages de l'application n'y figurent pas. Les autres fenêtres de Legendary Pen, comme les réglages, le menu de l'icône près de l'horloge ou le choix d'une zone de capture, y figurent si on les ouvre pendant l'enregistrement. Les effets de présentation sont suspendus le temps d'une capture de zone. Au relâchement du surligneur, les annotations et les effets peuvent manquer sur une image, le temps d'analyser le texte situé dessous.
- **Où vont les fichiers :** dans le dossier de capture choisi dans les réglages, en MP4 H.264 à 30 images par seconde, sous un nom comme `Legendary Pen 2026-11-03 21-10-05.mp4`. Une notification annonce la fin, et un clic dessus montre le fichier dans son dossier.
- **Le son :** il se choisit dans les réglages : aucun (par défaut), le son de l'ordinateur ou le micro, un seul à la fois, sans mélange. Il s'agit du périphérique par défaut de Windows au démarrage de la vidéo, enregistré en AAC stéréo à 48 kHz. Si le micro ou le son de l'ordinateur n'est pas disponible, la vidéo se fait sans son ; une notification le dit dès le début, puis à la fin. Il en va de même quand l'accès au micro est refusé, si Windows signale le refus. Le décompte rappelle le son choisi. Si le périphérique disparaît en route, la vidéo continue et la suite est muette.
- **Le micro :** il n'est ouvert que si on l'a choisi, et seulement le temps de l'enregistrement. Le reste du temps, Legendary Pen ne l'utilise jamais.
- **Arrêts automatiques :** l'enregistrement s'arrête proprement et garde le fichier s'il reste moins de 500 Mo sur le disque, si l'écran filmé est débranché ou si sa résolution change. Le PC et l'écran restent allumés pendant l'enregistrement ; si le PC se met quand même en veille, la vidéo s'arrête, et Legendary Pen attend 1,5 seconde pour la finir avant de laisser faire Windows : une vidéo plus longue à finir peut rester en `.tmp` à cet instant. Quitter l'application ou lancer une mise à jour finit d'abord la vidéo, en 15 secondes au plus. Un arrêt forcé du processus laisse en revanche un fichier `.tmp` illisible.
- **Si un fichier n'a pas pu être vérifié :** une vidéo n'est jamais supprimée pour cette raison. Le fichier reste sous son nom en `.tmp`, et la notification dit qu'il est peut-être incomplet. Seul un échec dû au disque plein retire un fichier partiel illisible, pour rendre la place.

Rien n'est envoyé : l'image et le son restent sur cet ordinateur ou dans le dossier choisi, et aucun droit administrateur n'est demandé.

## Signer un PDF

Le bouton « Signer un PDF » de la barre, aussi présent dans le menu de l'icône près de l'horloge, ouvre un document. On y trace sa signature ou on pose celle qu'on a mémorisée, on la déplace et on la redimensionne, puis on enregistre une copie. Le fichier d'origine n'est jamais modifié ; toutes les pages et leur texte sélectionnable sont conservés.

Cette signature est visuelle : elle n'a pas la valeur d'une signature électronique certifiée. Une signature mémorisée reste uniquement sur l'ordinateur, chiffrée pour la session Windows, et le bouton « Oublier ma signature » la supprime. La désinstallation efface aussi la signature mémorisée et les annotations conservées, mais garde les réglages. Les PDF protégés ou interdisant la modification sont refusés.

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

Les composants tiers et leurs licences sont listés dans `THIRD-PARTY-NOTICES.txt`.
