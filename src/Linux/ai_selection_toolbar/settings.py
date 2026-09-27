from __future__ import annotations

import json
import os
import re
import uuid
from copy import deepcopy
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

import keyring

from . import __author__, __version__

SERVICE_NAME = "ai-selection-toolbar"
DEFAULTS: dict[str, Any] = {
    "TimeoutSeconds": 120,
    "TranslationTargetLanguage": "中文",
    "ExcludedApplications": [],
    "AutoShow": True,
    "NotesDirectory": "",
    "StartOnLogin": False,
    "CustomActions": [],
    "ToolbarStyle": "standard",
    "ToolbarAccentColor": "#4F46E5",
    "ToolbarBackgroundColor": "#18202E",
    "ToolbarBorderColor": "#0B1020",
    "AnswerBackgroundColor": "#F8FAFC",
    "AnswerBorderColor": "#0B1020",
    "ApiProfiles": [],
    "ActiveApiId": "",
}


class SettingsError(ValueError):
    pass


def config_directory() -> Path:
    base = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
    return base / SERVICE_NAME


def data_directory() -> Path:
    base = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share"))
    return base / SERVICE_NAME


class SettingsStore:
    def __init__(self, directory: Path | None = None) -> None:
        self.directory = directory or config_directory()
        self.path = self.directory / "settings.json"
        self.legacy_secret_path = self.directory / "api-key"

    def load(self) -> dict[str, Any]:
        raw: dict[str, Any] = {}
        if self.path.exists():
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        settings = deepcopy(DEFAULTS)
        settings.update(raw)
        self._migrate_single_api(settings)
        self._normalize(settings)
        return settings

    def save(self, settings: dict[str, Any]) -> None:
        value = deepcopy(settings)
        self._normalize(value)
        active = active_profile(value)
        value["BaseUrl"] = active.get("BaseUrl", "") if active else ""
        value["Model"] = active.get("Model", "") if active else ""
        value["ProtectedApiKey"] = None
        for profile in value["ApiProfiles"]:
            profile.pop("ApiKey", None)
            profile.pop("ProtectedApiKey", None)
        self.directory.mkdir(parents=True, exist_ok=True)
        os.chmod(self.directory, 0o700)
        temporary = self.path.with_name(f"{self.path.name}.{uuid.uuid4().hex}.tmp")
        temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
        os.chmod(temporary, 0o600)
        temporary.replace(self.path)

    def api_key(self, profile_id: str) -> str:
        return keyring.get_password(SERVICE_NAME, profile_id) or ""

    def set_api_key(self, profile_id: str, api_key: str) -> None:
        if api_key:
            keyring.set_password(SERVICE_NAME, profile_id, api_key)
        else:
            try:
                keyring.delete_password(SERVICE_NAME, profile_id)
            except keyring.errors.PasswordDeleteError:
                pass

    def has_api_key(self, profile_id: str) -> bool:
        try:
            return bool(self.api_key(profile_id))
        except keyring.errors.KeyringError:
            return False

    def upsert_profile(self, payload: dict[str, Any]) -> str:
        settings = self.load()
        profile_id = str(payload.get("Id") or uuid.uuid4().hex)
        if profile_id != "legacy" and not re.fullmatch(r"[0-9a-f]{32}", profile_id):
            raise SettingsError("API 配置标识无效")
        name = str(payload.get("Name") or "").strip()
        base_url = str(payload.get("ApiBaseUrl") or "").strip().rstrip("/")
        model = str(payload.get("Model") or "").strip()
        validate_profile(name, base_url, model)
        profiles = settings["ApiProfiles"]
        profile = next((item for item in profiles if item["Id"] == profile_id), None)
        old_url = profile.get("BaseUrl", "") if profile else ""
        if profile is None:
            profile = {"Id": profile_id}
            profiles.append(profile)
        profile.update({"Name": name, "BaseUrl": base_url, "Model": model})
        api_key = str(payload.get("ApiKey") or "")
        if api_key:
            self.set_api_key(profile_id, api_key)
        elif old_url and old_url.rstrip("/") != base_url:
            self.set_api_key(profile_id, "")
        if bool(payload.get("MakeActive")) or not settings.get("ActiveApiId"):
            settings["ActiveApiId"] = profile_id
        self.save(settings)
        return profile_id

    def select_profile(self, profile_id: str) -> None:
        settings = self.load()
        if not any(item["Id"] == profile_id for item in settings["ApiProfiles"]):
            raise SettingsError("API 配置不存在")
        settings["ActiveApiId"] = profile_id
        self.save(settings)

    def delete_profile(self, profile_id: str) -> None:
        settings = self.load()
        profiles = settings["ApiProfiles"]
        if len(profiles) <= 1:
            raise SettingsError("至少保留一个 API 配置")
        remaining = [item for item in profiles if item["Id"] != profile_id]
        if len(remaining) == len(profiles):
            raise SettingsError("API 配置不存在")
        settings["ApiProfiles"] = remaining
        if settings["ActiveApiId"] == profile_id:
            settings["ActiveApiId"] = remaining[0]["Id"]
        self.set_api_key(profile_id, "")
        self.save(settings)

    def public_settings(self) -> dict[str, Any]:
        settings = self.load()
        result = deepcopy(settings)
        result["TargetLanguage"] = settings["TranslationTargetLanguage"]
        result["ApiBaseUrl"] = settings.get("BaseUrl", "")
        result["ApiProfiles"] = [
            {
                "Id": profile["Id"],
                "Name": profile["Name"],
                "ApiBaseUrl": profile["BaseUrl"],
                "Model": profile["Model"],
                "HasApiKey": self.has_api_key(profile["Id"]),
            }
            for profile in settings["ApiProfiles"]
        ]
        result["Platform"] = "Linux"
        result["Version"] = __version__
        result["Author"] = __author__
        result.pop("ProtectedApiKey", None)
        return result

    def _migrate_single_api(self, settings: dict[str, Any]) -> None:
        profiles = settings.get("ApiProfiles") or []
        if not profiles and (settings.get("BaseUrl") or settings.get("Model")):
            profiles = [{
                "Id": "legacy",
                "Name": "默认 API",
                "BaseUrl": settings.get("BaseUrl", ""),
                "Model": settings.get("Model", ""),
            }]
            settings["ApiProfiles"] = profiles
            settings["ActiveApiId"] = "legacy"
        if profiles and self.legacy_secret_path.exists():
            target_id = settings.get("ActiveApiId") or profiles[0].get("Id", "legacy")
            try:
                if not self.api_key(target_id):
                    legacy_key = self.legacy_secret_path.read_text(encoding="utf-8").strip()
                    if legacy_key:
                        self.set_api_key(target_id, legacy_key)
            except (OSError, keyring.errors.KeyringError):
                pass

    @staticmethod
    def _normalize(settings: dict[str, Any]) -> None:
        profiles = settings.get("ApiProfiles")
        if not isinstance(profiles, list):
            profiles = []
        normalized = []
        for profile in profiles:
            if not isinstance(profile, dict):
                continue
            profile_id = str(profile.get("Id") or uuid.uuid4().hex)
            normalized.append({
                "Id": profile_id,
                "Name": str(profile.get("Name") or "未命名 API"),
                "BaseUrl": str(profile.get("BaseUrl") or profile.get("ApiBaseUrl") or ""),
                "Model": str(profile.get("Model") or ""),
            })
        settings["ApiProfiles"] = normalized
        ids = {item["Id"] for item in normalized}
        if settings.get("ActiveApiId") not in ids:
            settings["ActiveApiId"] = normalized[0]["Id"] if normalized else ""
        active = active_profile(settings)
        settings["BaseUrl"] = active.get("BaseUrl", "") if active else ""
        settings["Model"] = active.get("Model", "") if active else ""


def active_profile(settings: dict[str, Any]) -> dict[str, Any] | None:
    active_id = settings.get("ActiveApiId")
    return next((item for item in settings.get("ApiProfiles", []) if item.get("Id") == active_id), None)


def validate_profile(name: str, base_url: str, model: str) -> None:
    if not name or len(name) > 80 or len(model) > 200 or len(base_url) > 2048:
        raise SettingsError("API 名称、地址或模型无效")
    parsed = urlparse(base_url)
    is_loopback = parsed.hostname in {"127.0.0.1", "localhost", "::1"}
    if not parsed.hostname or parsed.scheme not in ({"http", "https"} if is_loopback else {"https"}):
        raise SettingsError("远程 API 必须使用 HTTPS，本机回环地址可以使用 HTTP")
