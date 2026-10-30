# Qualification réelle Store et listes virtualisées

Préparation du 30 octobre 2026. Les fichiers de cette préparation n'ont pas été exécutés ; aucune preuve réelle
de virtualisation ou de suspension n'est revendiquée avant le passage du parent.

## Liste WPF réelle

`native-virtualized-wpf.ps1` crée une `ListBox` WPF standard, 3 000 objets de données, un
`VirtualizingStackPanel` et le mode Recycling. Aucun AutomationPeer ni fournisseur UIA personnalisé.
Une cible unique à l'index 2 048 et deux objets homonymes éloignés permettent les essais positif et négatif.

`native-virtualized-probe.ps1` est un second processus UIA en lecture seule. Avant tout geste, il doit prouver
que le moteur WPF installé expose réellement ItemContainer, que FindItemByProperty trouve la cible unique
avec VirtualizedItem, et que celle-ci reste absente de l'arbre comme des visuels de la fixture. Si ce n'est pas
le cas, l'essai échoue sans prétendre avoir qualifié la virtualisation. Le probe ne réalise ni ne sélectionne.

`native-virtualized-live.test.mjs` exige un binaire final explicite et l'opt-in
`NEXUS_NATIVE_VIRTUALIZED_TEST=1`. Le profil par défaut est Claude, paramétrable par
`NEXUS_NATIVE_VIRTUALIZED_AGENT`. Le parent l'exécute seul, sans autre geste GUI concurrent.

La preuve positive relie : cible initialement sans conteneur visuel → appel natif par nom → création du vrai
ListBoxItem → déplacement du ScrollViewer → SelectionChanged sur la clé du bon objet → état UIA sélectionné
relu par le client indépendant → Button.Click de validation enregistré exactement une fois. Le clic sur un
ListBoxItem peut utiliser SelectionItem.Select : l'oracle démontre son effet réel, sans prétendre un clic souris.
La commande homonyme doit être refusée sans sélection, validation ou matérialisation de la cible.

La preuve conservée sous `~/.nexus/nexus-windows/qualification/virtualized-*/receipt.json` n'est positive que
si `real_provider_qualified:true`. Des tests ignorés, une absence de motifs ou un timeout ne prouvent rien.

## Vraie application Store suspendue

Le parent peut utiliser la Calculatrice officielle comme application non sensible, sous les conditions suivantes :

1. Relever les processus, fenêtres, PID, naissance et paquet complets de la Calculatrice. **Si une instance de
   Julien existe, ne pas la suspendre, la fermer ou la réutiliser pour cet essai.** Consigner le test comme différé.
   Aucun lancement de profil factice ni changement du lifecycle global ne remplace cette condition.
2. Lancer normalement une nouvelle instance avec son AUMID exact fourni par `Get-StartApps` et l'API publique
   d'activation déjà qualifiée. Attribuer les nouveaux PID/paquet par naissance et filiation ; une fenêtre
   candidate seule ne prouve pas que cette instance appartient au test.
3. Garder l'application derrière, relever `IPackageDebugSettings.GetPackageExecutionState` par un processus
   observateur indépendant. Préférer une suspension naturelle : les états 2/3 doivent être réellement constatés.
   L'état 1 permanent signifie **non reproduit**, jamais « suspension réussie ».
4. Pour un essai déterministe, `IPackageDebugSettings.Suspend(packageFullName)` est une API publique possible,
   mais seulement si **tous les processus du paquet sont attribués à cette fixture nouvellement lancée**, sans
   autre fenêtre ou instance utilisateur. Recontrôler cette exclusivité immédiatement avant Suspend.
   Aucun `EnableDebugging`, modification PLM permanente, arrêt de threads ou outil de suspension privé.
5. L'observateur indépendant doit ensuite lire un état réellement suspendu (3, ou 2 puis 3). Aucun appel Nexus
   `look` pendant cette préparation, puisqu'il pourrait déjà provoquer la reprise. Relever l'identité de la
   fenêtre, le paquet et le premier plan avant le geste du moteur.
6. Appeler le vrai `look` du moteur final sur cette fenêtre. Son résultat doit indiquer une reprise demandée et
   un contenu prêt, puis présenter les contrôles réels de la Calculatrice. L'observateur indépendant relit l'état
   actif (1), les PID/naissances restent identiques et le premier plan initial n'a pas changé. L'appel `look` qui
   échoue, reprend un autre paquet, remplace un PID ou ne présente qu'un cadre vide n'est pas une réussite.
7. Fermer normalement seulement l'instance fixture attribuée. Si l'essai échoue après une suspension forcée,
   reprendre **le même paquet fixture** par `Resume` une seule fois pour restitution, contrôler l'état et fermer
   normalement ; ne jamais laisser une application suspendue par le banc. Pas de force-kill d'une application
   utilisateur, pas de deuxième essai automatique.

La Calculatrice distribuée sur ce PC peut être une application WinUI de bureau empaquetée non suspendable.
Dans ce cas le test est **non applicable à ce paquet**, même si `GetPackageFullName` réussit. Il faut une autre
application UWP de test déjà disponible et sans instance utilisateur, ou conserver cette limite matérielle.
Une CoreWindow masquée/cloquée n'est pas à elle seule la preuve d'un paquet suspendu ; ces deux propriétés
doivent être qualifiées séparément.

Sources primaires :

- [WPF ItemsControlAutomationPeer : motifs et enfants réalisés](https://source.dot.net/PresentationFramework/System/Windows/Automation/Peers/ItemsControlAutomationPeer.cs.html)
- [Microsoft : recherche d'éléments virtualisés](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-workingwithvirtualizeditems)
- [Microsoft : GetPackageExecutionState](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ipackagedebugsettings-getpackageexecutionstate)
- [Microsoft : Resume](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ipackagedebugsettings-resume)
- [Microsoft : Suspend](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ipackagedebugsettings-suspend)
