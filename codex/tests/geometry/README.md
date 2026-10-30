# Qualification géométrie et durée de vie

Ces tests créent leurs propres fenêtres/processus. Ils n'agissent sur aucune application de Julien.
Les captures et sorties de compilation restent hors de OneDrive. `physical.live.mjs` conserve le vrai
jeton de premier plan, l'arrêt d'urgence et la mesure Raw Input de présence ; son lancement est volontaire.

Depuis la racine de NexusWindows, compiler le helper et la fixture avec `dotnet build`, en passant pour chacun
`-o`, `BaseIntermediateOutputPath` et `BaseOutputPath` vers un dossier de `%LOCALAPPDATA%`. Les projets sont
`codex/native/NexusCodex.UIA.csproj` et `codex/tests/geometry/NexusWindows.GeometryFixture.csproj`.

```powershell
$env:NEXUS_NATIVE_TEST_EXE = 'C:\chemin-local\helper\NexusCodex.UIA.exe'
$env:NEXUS_GEOMETRY_FIXTURE = 'C:\chemin-local\fixture\NexusWindows.GeometryFixture.exe'
$env:NEXUS_NATIVE_PHYSICAL_TEST = '1'
node --test codex/tests/geometry/physical.live.mjs
python -B codex/tests/geometry/lifetime.live.py
```

Pillow est utilisé comme décodeur WebP indépendant. `NEXUS_TEST_PYTHON` peut nommer son interpréteur.
La première régression doit refuser le point périmé avec **zéro événement souris** ; l'ancien helper envoyait
un clic après le redimensionnement. Les autres cas comptent les événements dans la fixture, refusent une
surcouche sœur du même processus, vérifient la maximisation et la restitution du premier plan et décodent
les vrais pixels de captures couvertes. Les profils par écran et non adapté au DPI sont réellement distincts.

Le test de durée de vie place le vrai helper dans un job `KILL_ON_JOB_CLOSE`, lui fait lancer une fixture
sans fenêtre, ferme le job et vérifie que la fixture survit hors de ce job précis. Elle est ensuite arrêtée
par son handle attesté. Il crée aussi un bureau privé non affiché, sans Explorer, pour prouver un refus
avant le marqueur d'envoi et sans création de processus. Il ne change jamais le bureau affiché.

Qualification du 29 octobre : 3 tests physiques réussis, six WebP décodés, quatre clics sur les cibles et
zéro sur les surcouches ; profils 120 DPI et 96 DPI. Un seul écran physique était disponible. La durée de
vie est verte et le selftest Steam relit `STARTF_USESHOWWINDOW=1`, `SW_SHOWNOACTIVATE=4` dans son vrai enfant.
Ces résultats ne tranchent pas une divergence d'état propre à une application externe entre pixels et UIA.
