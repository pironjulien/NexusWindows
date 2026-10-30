"""Politique commune des garde-fous (garde-fous.json + garde_fous.py) : forme, décisions et échec fermé."""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import garde_fous  # noqa: E402


class PolicyFile(unittest.TestCase):
    def test_loads_and_validates(self):
        data = garde_fous.load()
        self.assertEqual(set(data["agents"]) - {"_"}, set(garde_fous.AGENTS))

    def test_missing_or_invalid_fails_closed(self):
        with tempfile.TemporaryDirectory() as tmp:
            missing = Path(tmp) / "absent.json"
            with self.assertRaises(garde_fous.GardeFousError):
                garde_fous.load(missing)
            broken = Path(tmp) / "casse.json"
            data = json.loads(garde_fous.POLICY_FILE.read_text(encoding="utf-8"))
            data["confirmation"]["motifs"].append("(non ferme")
            broken.write_text(json.dumps(data), encoding="utf-8")
            with self.assertRaises(garde_fous.GardeFousError):
                garde_fous.load(broken)
            unknown = Path(tmp) / "inconnu.json"
            data = json.loads(garde_fous.POLICY_FILE.read_text(encoding="utf-8"))
            data["agents"]["claude"].append("categorie_absente")
            unknown.write_text(json.dumps(data), encoding="utf-8")
            with self.assertRaises(garde_fous.GardeFousError):
                garde_fous.load(unknown)


class HardBlock(unittest.TestCase):
    def test_claude(self):
        for process, cls in [
            ("consent.exe", ""),
            ("LogonUI.exe", ""),
            ("Bitwarden.exe", ""),
        ]:
            self.assertTrue(garde_fous.hard_block_reason("claude", process, cls), process)
        for process in ("claude.exe", "antigravity.exe", "windowsterminal.exe", "explorer.exe", "codex.exe", "regedit.exe", "credentialuibroker.exe"):
            self.assertEqual(garde_fous.hard_block_reason("claude", process), "", process)
        self.assertEqual(garde_fous.hard_block_reason("claude", "x.exe", "Credential Dialog Xaml Host"), "")

    def test_antigravity_keeps_its_2_8_1_behaviour(self):
        self.assertTrue(garde_fous.hard_block_reason("antigravity", "lockapp.exe"))
        self.assertTrue(garde_fous.hard_block_reason("antigravity", "KeePassXC.exe"))
        self.assertEqual(garde_fous.hard_block_reason("antigravity", "claude.exe"), "")
        self.assertEqual(garde_fous.hard_block_reason("antigravity", "credentialuibroker.exe"), "")

    def test_codex_keeps_its_own_window_only(self):
        for process in ("Codex.exe", "ChatGPT.exe", "credentialuibroker.exe", "consent.exe", "Dashlane.exe", "NordPass.exe", "RoboForm.exe"):
            self.assertTrue(garde_fous.hard_block_reason("codex", process), process)
        for process in ("windowsterminal.exe", "openconsole.exe", "conhost.exe", "sechealthui.exe", "claude.exe", "explorer.exe"):
            self.assertEqual(garde_fous.hard_block_reason("codex", process), "", process)

    def test_unknown_agent_refused(self):
        with self.assertRaises(garde_fous.GardeFousError):
            garde_fous.hard_block_reason("inconnu", "notepad.exe")


class Confirmation(unittest.TestCase):
    def test_invisible_characters_and_lookalikes_do_not_hide_a_word(self):
        """27/10 : un caractère de format (zéro largeur, trait d'union conditionnel, marque de sens) ou une lettre
        cyrillique ou grecque sosie s'affichent comme le mot ; ils ne doivent pas le faire échapper aux motifs."""
        for label in ("Env\u200boyer", "En\u00advoyer", "\u200ePayer", "Pu\u2060blier", "\u0415nvoyer",
                      "\u0420ayer", "Supprimer d\u0435finitivement", "\u03a1ublier", "Env\ufe0foyer",
                      "Env\u034foyer", "E\u039dVOYER", "PA\u03a5ER"):  # fmt: skip
            self.assertTrue(garde_fous.confirm_word(label), ascii(label))
        self.assertEqual(garde_fous.plain("Env\u200boyer"), "envoyer")


    def test_every_lookalike_letter_is_detected(self):
        """Chaque sosie des deux tables, mis à la place de sa lettre latine dans un mot sortant, est détecté
        (régression du 27/10 : м, т, в, к, н, β, η retirés par erreur, rétablis)."""
        words = ("envoyer", "payer", "publier", "poster", "commander", "submit", "buy now", "checkout", "transfer",
                 "transferer", "send", "zap", "publish")  # fmt: skip
        checked = 0
        for table, upper in ((garde_fous.CONFUSABLES, False), (garde_fous.CONFUSABLES_UPPER, True)):
            for code, latin in table.items():
                lookalike, letter = chr(code), latin.lower()
                word = next((w for w in words if letter in w and garde_fous.outward_word(w)), None)
                if word is None:
                    continue
                label = word.upper().replace(letter.upper(), lookalike, 1) if upper else word.replace(letter, lookalike, 1)
                self.assertTrue(garde_fous.outward_word(label), f"{ascii(lookalike)} in {ascii(label)}")
                checked += 1
        self.assertGreater(checked, 45)
        # Non-régression : jamais une lettre de moins que l'ensemble relu le 27/10 (relecture de Codex).
        lower = "авекмнорстухіјѕԁԛԝһӏαβεηικνορτυχω"
        upper = "АВЕКМНОРСТУХІЈЅΑΒΕΖΗΙΚΜΝΟΡΤΥΧ"
        self.assertLessEqual({ord(c) for c in lower}, set(garde_fous.CONFUSABLES))
        self.assertLessEqual({ord(c) for c in upper}, set(garde_fous.CONFUSABLES_UPPER))
        for label in ("Co\u043cmander", "Pos\u0442er", "\u0432uy now", "chec\u043aout", "c\u043deckout", "Pa\u0443 now"):
            self.assertTrue(garde_fous.outward_word(label) or garde_fous.confirm_word(label), ascii(label))

    def test_words_to_confirm(self):
        for label in ("Envoyer", "Payer", "Publier", "Poster", "Supprimer définitivement", "Vider la corbeille",
                      "Formater", "Désinstaller", "Réinitialiser ce PC", "Résilier", "Supprimer mon compte",
                      "Rendre le dépôt public", "Make this repository public", "Place your order"):  # fmt: skip
            self.assertTrue(garde_fous.confirm_word(label), label)

    def test_wipe_reset_format_device(self):
        """Audit du 18/10 : effacer, réinitialiser ou formater tout l'appareil, un libellé par motif."""
        for label in ("Erase everything", "Reset phone", "Reset device",
                      "Reset tablet", "Reset PC", "Reset this PC", "Factory reset", "Formater le téléphone",
                      "Formater l’appareil", "Formater la tablette", "Format phone", "Format the device"):  # fmt: skip
            with self.subTest(label=label):
                self.assertTrue(garde_fous.confirm_word(label), label)
        for label in ("Reset filters", "Format", "Effacer", "Tout sélectionner"):
            with self.subTest(label=label):
                self.assertEqual(garde_fous.confirm_word(label), "", label)
        # « Tout effacer » : libre (calculatrice, filtres), confirmé sur un écran de réinitialisation.
        for label in ("Tout effacer", "Effacer tout", "Tout supprimer"):
            with self.subTest(label=label):
                self.assertEqual(garde_fous.confirm_word(label, "Calculatrice"), "", label)
                self.assertTrue(garde_fous.confirm_word(label, "Réinitialisation des données d'usine"), label)
                self.assertTrue(garde_fous.confirm_word(label, "Effacer toutes les données"), label)

    def test_free_words(self):
        for label in ("Éléments envoyés", "Boîte d'envoi", "Supprimer", "Valider", "OK", "Déployer",
                      "Réinitialiser les filtres", "Réinitialiser le formulaire", "Réinitialiser les champs",
                      "Réinitialiser le zoom", "Formater le document", "Envoyé le 12/10"):  # fmt: skip
            self.assertEqual(garde_fous.confirm_word(label), "", label)

    def test_post_subscribe_book_answer_price(self):
        """Revue du 19/10 : boutons qui publient, abonnent, réservent, donnent, décrochent ou paient sans le dire."""
        for label in ("Post", "Reply", "Répondre", "Tweet", "S'abonner", "Subscribe", "Start free trial",
                      "Essai gratuit", "Commencer l'essai", "Annuler l'abonnement", "Book now", "Book", "Réserver",
                      "Donate", "Faire un don", "Answer", "Décrocher", "Accept call", "Dial", "Transférer",
                      "4,99 €", "€4.99", "$12", "12,50 EUR", "CHF 20", "£3.99"):  # fmt: skip
            with self.subTest(label=label):
                self.assertTrue(garde_fous.confirm_word(label), label)
        for label in ("Posts", "Facebook", "Notebook", "Mes réservations", "Réponses", "2 messages", "Page 3",
                      "Version 1.2", "Post office", "Abonnements"):  # fmt: skip
            with self.subTest(label=label):
                self.assertEqual(garde_fous.confirm_word(label), "", label)

    def test_reset_confirmed_unless_harmless(self):
        """Accord Claude–Antigravity du 19/10 : le bouton final d'One UI ou de Windows s'appelle souvent juste
        « Réinitialiser » ; seule une liste d'actions inoffensives (filtres, formulaire, zoom...) passe librement."""
        for label in ("Réinitialiser", "Réinitialiser les paramètres réseau", "Réinitialiser tous les paramètres",
                      "Réinitialiser l'application", "Réinitialiser le téléphone",
                      "Réinitialisation des données d'usine"):  # fmt: skip
            with self.subTest(label=label):
                self.assertTrue(garde_fous.confirm_word(label), label)

    def test_generic_verb_only_in_money_or_format_window(self):
        self.assertEqual(garde_fous.confirm_word("OK", "Bloc-notes"), "")
        self.assertIn("paiement", garde_fous.confirm_word("Valider", "Confirmer le paiement"))
        self.assertIn("formatage", garde_fous.confirm_word("Démarrer", "Formater Disque local (E:)"))

    def test_empty_patterns_disable(self):
        self.assertEqual(garde_fous.confirm_word("Envoyer", patterns=()), "")

    def test_check_blind_paste(self):
        # Lot sain : seulement un collage
        self.assertEqual(garde_fous.check_blind_paste([{"action": "key", "key": "ctrl+v"}]), "")
        # Lot sain : collage puis relecture/clic ailleurs sans envoi
        self.assertEqual(garde_fous.check_blind_paste([{"action": "key", "key": "ctrl+v"}, {"action": "wait", "seconds": 1}]), "")
        # Lot interdit : collage puis Entrée
        err = garde_fous.check_blind_paste([
            {"action": "click", "target": "Champ"},
            {"action": "key", "key": "ctrl+v"},
            {"action": "wait", "seconds": 1},
            {"action": "key", "key": "Enter"},
        ])
        self.assertIn("interdiction de chaîner un collage", err)
        # Lot interdit : collage puis confirm=True sur un bouton
        err2 = garde_fous.check_blind_paste([
            {"action": "key", "key": "Control+V"},
            {"action": "click", "target": "Envoyer", "confirm": True},
        ])
        self.assertIn("interdiction de chaîner un collage", err2)


if __name__ == "__main__":
    unittest.main()

