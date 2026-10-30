"""Installation du plugin `nexus-windows` dans Antigravity (`~/.gemini/config/plugins/nexus-windows`), en une commande :

    uvx --python 3.14 nexus-windows-antigravity install      (depuis PyPI : aucun clone, ni PowerShell 7, ni Git)

Le plugin (guide de l'agent, manifeste) est copié depuis le paquet ou le clone ; son `mcp_config.json` déclare le
serveur une seule fois : `uvx --python 3.14 nexus-windows-antigravity@latest` (chemin absolu de uvx, pour un
Antigravity déjà ouvert dont le PATH est ancien), ou, depuis un clone (`install.ps1`), le même EXE C#
immutable que Codex et Claude, avec `--mcp --agent antigravity`. Le lanceur privé reste un secours explicite. Un plugin découvert est actif par défaut (documentation embarquée d'Antigravity :
`config.json` ne garde qu'un choix explicite de l'utilisateur) : `config.json` n'est écrit que pour reporter sous le
nouveau nom un plugin désactivé sous l'ancien, un plugin que l'utilisateur a désactivé est seulement signalé. Les anciennes déclarations en double (entrée du `mcp_config.json`
global, copie globale `skills/nexus-windows`) sont retirées ; les autres serveurs, plugins et clés restent intacts.
Migration d'identité (28/10/2026) : le plugin s'appelait `nexus-computer` ; son dossier est vidé AVANT la pose du
plugin `nexus-windows` (un échec laisse l'ancien seul, jamais deux serveurs du même moteur déclarés), sa copie globale
`skills/nexus-computer` et son entrée globale sont retirées, et un « désactivé » choisi sous l'ancien nom est gardé.
`--check` contrôle sans rien écrire ; `uninstall` retire le plugin. `--guide` (clone privé de Julien) installe son guide en
français (`guide/SKILL.fr.md`, non exporté) à la place du guide public anglais."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import uuid
from pathlib import Path

PLUGIN = "nexus-windows"
PACKAGE = "nexus-windows-antigravity"
# Ancien nom du plugin (jusqu'au 28/10/2026), retiré à l'installation du nouveau.
LEGACY_PLUGINS = ("nexus-computer",)
# Anciennes déclarations globales du même serveur : celle de l'installeur d'avant le plugin (sous l'ancien nom et le
# nouveau), et celle que le README faisait écrire à la main pour l'installation depuis PyPI (3.0 à 3.1.3).
GLOBAL_DUPLICATES = (PLUGIN, *LEGACY_PLUGINS, PACKAGE)
GUIDE = f"skills/{PLUGIN}/SKILL.md"
HERE = Path(__file__).resolve().parent


def remove_tree(folder: Path) -> None:
    """Retire un dossier de plugin. Antigravity ouvert garde une poignée sur le dossier lui-même : vidé, il ne déclare
    plus rien et part au passage suivant, Antigravity fermé. Un fichier verrouillé, lui, fait échouer."""
    for file in (p for p in folder.rglob("*") if p.is_file()):
        file.unlink()
    try:
        shutil.rmtree(folder)
    except PermissionError:
        pass


class Drift(Exception):
    """Contrôle (`--check`) : l'installation n'est pas alignée."""


def config_root() -> Path:
    return Path(os.environ.get("USERPROFILE") or Path.home()) / ".gemini" / "config"


def plugin_source() -> Path:
    """Plugin livré : `plugin/` dans le paquet PyPI, `plugins/nexus-windows` dans un clone."""
    for candidate in (HERE / "plugin", HERE.parent / "plugins" / PLUGIN):
        if (candidate / "plugin.json").is_file():
            return candidate
    raise FileNotFoundError(f"plugin.json of {PLUGIN} not found next to {HERE}")


def server_declaration(launcher: Path | None) -> dict:
    """Déclaration unique du serveur, écrite dans le `mcp_config.json` du plugin."""
    if launcher is not None:
        manifest = Path(os.environ.get("USERPROFILE") or Path.home()) / ".nexus" / "nexus-windows" / "installation.json"
        runtime = json.loads(manifest.read_text(encoding="utf-8"))
        executable = Path(runtime["NativeExecutable"])
        if runtime.get("Schema") != 2 or not executable.is_file():
            raise ValueError("Common native runtime not installed")
        return {"command": str(executable), "args": ["--mcp", "--agent", "antigravity"]}
    uvx = shutil.which("uvx") or "uvx"
    # @latest : chaque démarrage d'Antigravity lance la dernière version publiée (mise à jour sans l'appli Nexus ni
    # réinstallation ; contrat « mises à jour hors conteneur » du 26/10). Le serveur écrit son état au démarrage.
    return {
        "command": uvx,
        "args": ["--python", "3.14", f"{PACKAGE}@latest"],
        "env": {"PYTHONUTF8": "1"},
    }


def owned_server(server: object, launcher: Path | None = None) -> bool:
    if not isinstance(server, dict) or server.get("disabled") is True or server.get("enabled") is False:
        return False
    if set(server) - {"command", "args", "env"}:
        return False
    command, args = str(server.get("command", "")), server.get("args", [])
    if not isinstance(args, list):
        return False
    import ntpath

    name = ntpath.basename(command).lower()
    if name == "nexuswindows.exe":
        import re

        base = ntpath.join(os.environ.get("USERPROFILE") or str(Path.home()), ".nexus", "nexus-windows", "native")
        try:
            parts = ntpath.relpath(command, base).split(ntpath.sep)
        except ValueError:
            return False
        return (
            args == ["--mcp", "--agent", "antigravity"]
            and not server.get("env")
            and len(parts) == 2
            and re.fullmatch(r"[a-f0-9]{64}", parts[0]) is not None
        )
    if name in {"pwsh", "pwsh.exe", "powershell.exe"}:
        return (
            len(args) == 3
            and args[:2] == ["-NoProfile", "-File"]
            and launcher is not None
            and ntpath.normcase(str(args[2])) == ntpath.normcase(str(launcher))
            and not server.get("env")
        )
    return (
        name in {"uvx", "uvx.exe"}
        and args == ["--python", "3.14", f"{PACKAGE}@latest"]
        and server.get("env", {"PYTHONUTF8": "1"}) == {"PYTHONUTF8": "1"}
    )


class Installer:
    def __init__(self, check: bool) -> None:
        self.check = check
        self.changes = 0
        self.root = config_root()
        self.target = self.root / "plugins" / PLUGIN

    def write(self, path: Path, data: bytes) -> None:
        if path.is_file():
            current = path.read_bytes()
            if current == data:
                return
            if path.suffix == ".json":
                try:
                    if json.loads(current) == json.loads(data):
                        return
                except ValueError:
                    pass
        if self.check:
            raise Drift(f"not aligned: {path}")
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_name(f".{PLUGIN}-{uuid.uuid4().hex}")
        try:
            temporary.write_bytes(data)
            os.replace(temporary, path)
        finally:
            temporary.unlink(missing_ok=True)
        self.changes += 1

    def remove(self, path: Path) -> None:
        if self.check:
            raise Drift(f"obsolete file: {path}")
        path.unlink()
        self.changes += 1

    def install(self, launcher: Path | None, guide: Path | None = None) -> None:
        source = plugin_source()
        # 0. Ancien nom : un « désactivé » choisi par l'utilisateur est reporté sur le nouveau nom (sinon le nouveau
        #    plugin, découvert, serait actif) ; puis l'ancien plugin est vidé, avant toute pose du nouveau : un échec
        #    laisse l'ancien seul, jamais deux déclarations du même moteur.
        self.carry_disabled_choice()
        for name in LEGACY_PLUGINS:
            legacy_plugin = self.root / "plugins" / name
            if not legacy_plugin.is_dir():
                continue
            if any(p.is_file() for p in legacy_plugin.rglob("*")):
                if self.check:
                    raise Drift(f"former plugin still installed: {legacy_plugin}")
                self.changes += 1
            if not self.check:
                remove_tree(legacy_plugin)
        # 1. Fichiers du plugin, copiés octet pour octet ; ceux que la version livrée n'a plus sont retirés.
        shipped = set()
        for file in sorted(source.rglob("*")):
            relative = file.relative_to(source)
            if not file.is_file() or relative.name == "mcp_config.json" or "__pycache__" in relative.parts:
                continue
            shipped.add(relative)
            data = guide.read_bytes() if guide is not None and relative == Path(GUIDE) else file.read_bytes()
            self.write(self.target / relative, data)
        # Un fichier illisible (chemin de plus de 260 caractères sans les chemins longs de Windows) manquerait sans
        # bruit : le plugin sans son guide n'est pas installé.
        missing = {Path("plugin.json"), Path(GUIDE)} - shipped
        if missing:
            raise FileNotFoundError(f"plugin files not readable in {source}: {', '.join(map(str, sorted(missing)))}")
        if self.target.is_dir():
            for file in sorted(self.target.rglob("*")):
                relative = file.relative_to(self.target)
                if file.is_file() and relative.name != "mcp_config.json" and relative not in shipped:
                    self.remove(file)
            if not self.check:
                for folder in sorted((p for p in self.target.rglob("*") if p.is_dir()), reverse=True):
                    if not any(folder.iterdir()):
                        folder.rmdir()
        # 2. La déclaration unique du serveur.
        declaration = {"mcpServers": {PLUGIN: server_declaration(launcher)}}
        existing = self.target / "mcp_config.json"
        if existing.is_file():
            previous = json.loads(existing.read_text(encoding="utf-8"))
            servers = previous.get("mcpServers", {})
            current = servers.get(PLUGIN)
            if current is not None and not owned_server(current, launcher):
                declaration = previous  # custom/disabled MCP: guide updates, declaration stays exact
            else:
                previous.setdefault("mcpServers", {})[PLUGIN] = declaration["mcpServers"][PLUGIN]
                declaration = previous
        self.write(existing, (json.dumps(declaration, indent=2) + "\n").encode("utf-8"))
        # 3. Doublons anciens : serveur déclaré aussi dans le mcp_config.json global (outils en double), guide copié
        #    aussi dans les compétences globales.
        global_mcp = self.root / "mcp_config.json"
        if global_mcp.is_file():
            content = json.loads(global_mcp.read_text(encoding="utf-8"))
            servers = content.get("mcpServers") if isinstance(content, dict) else None
            if isinstance(servers, dict) and any(name in servers for name in GLOBAL_DUPLICATES):
                for name in GLOBAL_DUPLICATES:
                    if owned_server(servers.get(name), launcher):
                        servers.pop(name, None)
                self.write(global_mcp, (json.dumps(content, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))
        for name in (PLUGIN, *LEGACY_PLUGINS):
            legacy_skill = self.root / "skills" / name / "SKILL.md"
            if legacy_skill.is_file():
                self.remove(legacy_skill)
                if not any(legacy_skill.parent.iterdir()):
                    legacy_skill.parent.rmdir()

    def carry_disabled_choice(self) -> None:
        settings = self.root / "config.json"
        try:
            content = json.loads(settings.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return
        plugins = content.get("plugins") if isinstance(content, dict) else None
        if not isinstance(plugins, dict) or PLUGIN in plugins:
            return
        if any(
            isinstance(plugins.get(name), dict) and plugins[name].get("enabled") is False for name in LEGACY_PLUGINS
        ):
            plugins[PLUGIN] = {"enabled": False}
            self.write(settings, (json.dumps(content, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))

    def disabled_by_user(self) -> bool:
        settings = self.root / "config.json"
        try:
            entry = json.loads(settings.read_text(encoding="utf-8")).get("plugins", {}).get(PLUGIN)
        except (OSError, ValueError, AttributeError):
            return False
        return isinstance(entry, dict) and entry.get("enabled") is False


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog=f"{PACKAGE} install",
        description="Install the nexus-windows plugin (agent guide + MCP server) into Google Antigravity.",
    )
    parser.add_argument("action", choices=["install", "uninstall"])
    parser.add_argument("--check", action="store_true", help="check the installation without writing anything")
    parser.add_argument("--launcher", type=Path, help="development clone: start the server with this PowerShell script")
    parser.add_argument(
        "--guide", type=Path, help="development clone: install this agent guide instead of the shipped one"
    )
    args = parser.parse_args(argv)
    installer = Installer(check=args.check)
    if args.action == "uninstall":
        if not args.check:
            for folder in (installer.target, *(installer.root / "plugins" / name for name in LEGACY_PLUGINS)):
                if folder.is_dir():
                    remove_tree(folder)
        print(f"NexusWindows for Antigravity: plugin removed from {installer.target}. Restart Antigravity.")
        return 0
    try:
        installer.install(
            args.launcher.resolve() if args.launcher else None, args.guide.resolve() if args.guide else None
        )
    except Drift as drift:
        print(f"NexusWindows for Antigravity: {drift}. Run the install command again.", file=sys.stderr)
        return 1
    if args.check:
        state = "up to date"
    elif installer.changes:
        state = "installed. Restart Antigravity (or open a new conversation) to load it"
    else:
        state = "already up to date"
    print(f"NexusWindows for Antigravity: plugin {PLUGIN} {state} ({installer.target}).")
    if installer.disabled_by_user():
        print("The plugin is turned off in Antigravity: turn it on in Customizations > Installed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
