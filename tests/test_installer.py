"""Windows PowerShell 5.1 installer regressions, without downloads or service changes."""

import ctypes
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import uuid


INSTALLER = Path(__file__).resolve().parents[1] / "install.ps1"


def ps_string(value):
    return "'" + str(value).replace("'", "''") + "'"


class InstallerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if os.name != "nt":
            raise unittest.SkipTest("Windows PowerShell 5.1 is required")

        if not ctypes.windll.shell32.IsUserAnAdmin():
            raise unittest.SkipTest("Run from an administrator terminal, as required by install.ps1")

        cls.root = Path(tempfile.mkdtemp(prefix="codex-installer-regression-"))
        cls.powershell = str(Path(os.environ["SystemRoot"]) / "System32/WindowsPowerShell/v1.0/powershell.exe")
        print("Installer regression artifacts:", cls.root, flush=True)

    def setUp(self):
        self.work = self.root / self._testMethodName
        self.work.mkdir()
        self.install_dir = self.work / "runner"
        self.service = "CodexInstallerProbe" + uuid.uuid4().hex

    def execute(self, arguments, *, host_noninteractive=False, real_console=False):
        args = [self.powershell, "-NoProfile", "-ExecutionPolicy", "Bypass"]
        if host_noninteractive:
            args.append("-NonInteractive")

        stdout_path = self.work / "stdout.bin"
        stderr_path = self.work / "stderr.bin"
        if real_console:
            startup = subprocess.STARTUPINFO()
            startup.dwFlags = subprocess.STARTF_USESHOWWINDOW
            startup.wShowWindow = 0
            process = subprocess.Popen(args + arguments, creationflags=subprocess.CREATE_NEW_CONSOLE, startupinfo=startup)
            try:
                code = process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
                self.fail("Installer blocked in hidden console")

            return code, stdout_path.read_bytes() if stdout_path.exists() else b"", b""

        with stdout_path.open("wb") as stdout, stderr_path.open("wb") as stderr:
            process = subprocess.Popen(args + arguments, stdin=subprocess.PIPE, stdout=stdout,
                                       stderr=stderr, creationflags=subprocess.CREATE_NO_WINDOW)
            try:
                code = process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
                self.fail("Installer blocked while redirected stdin remained open")
            finally:
                process.stdin.close()

        return code, stdout_path.read_bytes(), stderr_path.read_bytes()

    def probe(self, options=None, *, config=None, environment=None, host_noninteractive=False,
              scriptblock=False, real_console=False, answers=None):
        options = options or {}
        if config is not None:
            self.install_dir.mkdir()
            (self.install_dir / "appsettings.json").write_text(json.dumps(config), encoding="utf-8-sig")
            (self.install_dir / "Zapqio.Runner.exe").write_bytes(b"unchanged-installation-fixture")

        parameters = {"InstallDir": str(self.install_dir), "ServiceName": self.service, "Version": "test", **options}
        entries = []
        for key, value in parameters.items():
            literal = "$true" if value is True else ps_string(value)
            entries.append(key + " = " + literal)

        environment_literals = ", ".join(ps_string(entry) for entry in (environment or []))
        observations = self.work / "observations.json"
        invocation = "& " + ps_string(INSTALLER) + " @parameters"
        if scriptblock:
            invocation = "& ([scriptblock]::Create([IO.File]::ReadAllText(" + ps_string(INSTALLER) + ").TrimStart([char]0xFEFF))) @parameters"

        if real_console:
            invocation += " *>&1 | Out-File -Encoding utf8 -FilePath " + ps_string(self.work / "stdout.bin")

        answer_literals = ", ".join(ps_string(answer) for answer in (answers or []))

        wrapper = self.work / "probe.ps1"
        wrapper.write_text(f"""$ErrorActionPreference = 'Stop'
$global:PromptCalls = 0
$global:DownloadCalls = 0
$global:RestCalls = 0
$global:PromptAnswers = @({answer_literals})
function global:Read-Host {{
    $global:PromptCalls++
    if ($global:PromptCalls -le $global:PromptAnswers.Count) {{
        return $global:PromptAnswers[$global:PromptCalls - 1]
    }}

    throw 'UNEXPECTED_PROMPT'
}}

function global:Invoke-WebRequest {{
    $global:DownloadCalls++
    throw 'DOWNLOAD_REACHED'
}}

function global:Invoke-RestMethod {{
    $global:RestCalls++
    throw 'UNEXPECTED_RELEASE_LOOKUP'
}}

function global:Get-ItemProperty {{
    return [pscustomobject]@{{ Environment = @({environment_literals}) }}
}}

[Console]::OutputEncoding = [Text.Encoding]::GetEncoding(852)
$initialEncoding = [Console]::OutputEncoding.CodePage
$inputRedirected = [Console]::IsInputRedirected
$outputRedirected = [Console]::IsOutputRedirected
$parameters = @{{ {'; '.join(entries)} }}
$failure = $null
try {{
    {invocation}
}} catch {{
    $failure = $_.Exception.Message
}} finally {{
    $observation = [pscustomobject]@{{
        prompts = $global:PromptCalls
        downloads = $global:DownloadCalls
        releaseLookups = $global:RestCalls
        initialEncoding = $initialEncoding
        finalEncoding = [Console]::OutputEncoding.CodePage
        inputRedirected = $inputRedirected
        outputRedirected = $outputRedirected
        failure = $failure
    }}
    [IO.File]::WriteAllText({ps_string(observations)}, ($observation | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
}}
""", encoding="utf-8-sig")
        code, stdout, stderr = self.execute(["-File", str(wrapper)], host_noninteractive=host_noninteractive, real_console=real_console)
        self.assertEqual(code, 0, stderr.decode("cp852", errors="replace"))
        result = json.loads(observations.read_text(encoding="utf-8"))
        self.assertEqual(result["prompts"], len(answers or []), result)
        self.assertEqual(result["releaseLookups"], 0, result)
        self.assertEqual(result["initialEncoding"], result["finalEncoding"], result)
        self.assertIn("Język instalacji".encode("utf-8"), stdout)
        self.assertNotIn("Język instalacji".encode("cp852"), stdout)
        if real_console:
            self.assertFalse(result["inputRedirected"], result)
            self.assertFalse(result["outputRedirected"], result)

        if config is not None:
            self.assertEqual(json.loads((self.install_dir / "appsettings.json").read_text(encoding="utf-8-sig")), config)
            self.assertEqual((self.install_dir / "Zapqio.Runner.exe").read_bytes(), b"unchanged-installation-fixture")
        else:
            self.assertFalse(self.install_dir.exists())

        return result

    def assert_missing(self, result, name):
        self.assertEqual(result["downloads"], 0, result)
        self.assertIn("Brakuje konfiguracji", result["failure"])
        self.assertIn(name, result["failure"])

    def test_complete_instance_parameters_skip_all_prompts(self):
        result = self.probe({"Instance": "test", "Token": "fixture-token"})
        self.assertEqual(result["downloads"], 1, result)
        self.assertIn("DOWNLOAD_REACHED", result["failure"])

    def test_complete_url_parameters_skip_all_prompts(self):
        result = self.probe({"Url": "ws://localhost/test", "Token": "fixture-token"})
        self.assertEqual(result["downloads"], 1, result)

    def test_complete_parameters_skip_prompts_in_attached_console(self):
        result = self.probe({"Instance": "test", "Token": "fixture-token"}, real_console=True)
        self.assertEqual(result["downloads"], 1, result)

    def test_unattended_skips_prompts_in_attached_console(self):
        result = self.probe({"Unattended": True}, config={"Token": "fixture-token", "Url": "ws://localhost/test"}, real_console=True)
        self.assertEqual(result["downloads"], 1, result)

    def test_host_noninteractive_skips_prompts_in_attached_console(self):
        self.assert_missing(self.probe(host_noninteractive=True, real_console=True), "Token")

    def test_interactive_installation_still_reads_answers(self):
        result = self.probe(real_console=True, answers=["pl", "test", "fixture-token", "", ""])
        self.assertEqual(result["downloads"], 1, result)

    def test_redirected_input_missing_settings_fails_before_download(self):
        self.assert_missing(self.probe(), "Token")

    def test_unattended_missing_token_preserves_existing_files(self):
        self.assert_missing(self.probe({"Unattended": True}, config={"Url": "ws://localhost/test", "Token": ""}), "Token")

    def test_unattended_missing_url_preserves_existing_files(self):
        self.assert_missing(self.probe({"Unattended": True}, config={"Token": "fixture-token"}), "Url")

    def test_whitespace_settings_are_missing(self):
        self.assert_missing(self.probe({"Unattended": True, "Token": "   "},
            config={"Token": "   ", "Url": "  "}, environment=["ZAPQIO_TOKEN= ", "ZAPQIO_URL= "]), "Token")

    def test_host_noninteractive_missing_settings_fails_before_download(self):
        self.assert_missing(self.probe(host_noninteractive=True), "Token")

    def test_unattended_reuses_saved_configuration(self):
        config = {"Token": "fixture-token", "Url": "ws://localhost/test", "Name": "existing", "MaxConcurrency": 7}
        result = self.probe({"Unattended": True}, config=config)
        self.assertEqual(result["downloads"], 1, result)

    def test_unattended_reuses_service_environment(self):
        result = self.probe({"Unattended": True}, environment=["ZAPQIO_TOKEN=fixture-token", "ZAPQIO_URL=ws://localhost/test"])
        self.assertEqual(result["downloads"], 1, result)

    def test_supplied_token_completes_saved_configuration(self):
        result = self.probe({"Unattended": True, "Token": "fixture-token"}, config={"Url": "ws://localhost/test"})
        self.assertEqual(result["downloads"], 1, result)

    def test_downloaded_scriptblock_restores_caller_encoding(self):
        result = self.probe({"Unattended": True}, scriptblock=True)
        self.assert_missing(result, "Token")

    def test_direct_file_failure_has_nonzero_exit_and_utf8_stderr(self):
        code, stdout, stderr = self.execute(["-File", str(INSTALLER), "-Unattended",
            "-InstallDir", str(self.install_dir), "-ServiceName", self.service])
        self.assertNotEqual(code, 0)
        self.assertIn("Język instalacji".encode("utf-8"), stdout)
        self.assertIn("usłudze".encode("utf-8"), stderr)
        self.assertNotIn("usłudze".encode("cp852"), stderr)
        self.assertFalse(self.install_dir.exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
