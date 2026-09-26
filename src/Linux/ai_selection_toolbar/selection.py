from __future__ import annotations

import ctypes
import subprocess
import time
from ctypes import POINTER, Structure, byref, c_char_p, c_int, c_uint, c_ulong, c_ubyte, c_void_p

from PySide6.QtCore import QThread, Signal

BUTTON1_MASK = 1 << 8


class XClassHint(Structure):
    _fields_ = [("res_name", c_void_p), ("res_class", c_void_p)]


class SelectionWatcher(QThread):
    selection_captured = Signal(str, str, str, int, int)
    selection_cleared = Signal(int, int)
    failed = Signal(str)

    def __init__(self, excluded: list[str] | None = None) -> None:
        super().__init__()
        self.excluded = {normalize(value) for value in excluded or []}
        self._stopping = False

    def update_excluded(self, values: list[str]) -> None:
        self.excluded = {normalize(value) for value in values}

    def stop(self) -> None:
        self._stopping = True
        self.wait(1800)

    def run(self) -> None:
        try:
            x11 = ctypes.CDLL("libX11.so.6")
            self._configure_x11(x11)
            display = x11.XOpenDisplay(None)
            if not display:
                self.failed.emit("无法连接 X11 显示服务")
                return
            root = x11.XDefaultRootWindow(display)
            space = x11.XKeysymToKeycode(display, 0x20)
            ctrl_left = x11.XKeysymToKeycode(display, 0xFFE3)
            ctrl_right = x11.XKeysymToKeycode(display, 0xFFE4)
            shift_left = x11.XKeysymToKeycode(display, 0xFFE1)
            shift_right = x11.XKeysymToKeycode(display, 0xFFE2)
            pressed_before = False
            hotkey_before = False
            down_x = down_y = 0
            try:
                while not self._stopping:
                    root_return = c_ulong()
                    child_return = c_ulong()
                    root_x = c_int()
                    root_y = c_int()
                    win_x = c_int()
                    win_y = c_int()
                    mask = c_uint()
                    ok = x11.XQueryPointer(display, root, byref(root_return), byref(child_return),
                                           byref(root_x), byref(root_y), byref(win_x), byref(win_y), byref(mask))
                    if ok:
                        pressed = bool(mask.value & BUTTON1_MASK)
                        if pressed and not pressed_before:
                            down_x, down_y = root_x.value, root_y.value
                            self.selection_cleared.emit(root_x.value, root_y.value)
                        if (not pressed and pressed_before and
                                (abs(root_x.value - down_x) > 5 or abs(root_y.value - down_y) > 5)):
                            time.sleep(0.07)
                            self._capture(x11, display, root_x.value, root_y.value)
                        pressed_before = pressed
                        keys = (c_ubyte * 32)()
                        x11.XQueryKeymap(display, keys)
                        hotkey = (self._is_down(keys, space) and
                                  (self._is_down(keys, ctrl_left) or self._is_down(keys, ctrl_right)) and
                                  (self._is_down(keys, shift_left) or self._is_down(keys, shift_right)))
                        if hotkey and not hotkey_before:
                            self._capture(x11, display, root_x.value, root_y.value)
                        hotkey_before = hotkey
                    time.sleep(0.025)
            finally:
                x11.XCloseDisplay(display)
        except Exception as error:
            self.failed.emit(str(error))

    def _capture(self, x11: ctypes.CDLL, display: int, x: int, y: int) -> None:
        focus = c_ulong()
        revert = c_int()
        x11.XGetInputFocus(display, byref(focus), byref(revert))
        if focus.value in {0, 1}:
            return
        application, title = self._window_identity(x11, display, focus.value)
        if not application or normalize(application) in self.excluded:
            return
        try:
            result = subprocess.run(
                ["xclip", "-selection", "primary", "-out", "-t", "UTF8_STRING"],
                check=False, capture_output=True, timeout=0.8,
            )
        except (FileNotFoundError, subprocess.TimeoutExpired):
            return
        if result.returncode != 0 or len(result.stdout) > 131072:
            return
        text = result.stdout.decode("utf-8", errors="replace").strip()
        if text:
            self.selection_captured.emit(text, application, title, x, y)

    @staticmethod
    def _window_identity(x11: ctypes.CDLL, display: int, window: int) -> tuple[str, str]:
        for _ in range(24):
            hint = XClassHint()
            if x11.XGetClassHint(display, window, byref(hint)):
                try:
                    application = ctypes.string_at(hint.res_class).decode("utf-8", errors="replace") if hint.res_class else ""
                finally:
                    if hint.res_name:
                        x11.XFree(hint.res_name)
                    if hint.res_class:
                        x11.XFree(hint.res_class)
                if application:
                    name = c_void_p()
                    title = ""
                    if x11.XFetchName(display, window, byref(name)) and name.value:
                        try:
                            title = ctypes.string_at(name.value).decode("utf-8", errors="replace")
                        finally:
                            x11.XFree(name)
                    return application, title
            root = c_ulong()
            parent = c_ulong()
            children = POINTER(c_ulong)()
            count = c_uint()
            if not x11.XQueryTree(display, window, byref(root), byref(parent), byref(children), byref(count)):
                break
            if children:
                x11.XFree(children)
            if parent.value == window or parent.value == 0:
                break
            window = parent.value
        return "", ""

    @staticmethod
    def _is_down(keys: ctypes.Array[c_ubyte], keycode: int) -> bool:
        return 0 < keycode < 256 and bool(keys[keycode // 8] & (1 << (keycode % 8)))

    @staticmethod
    def _configure_x11(x11: ctypes.CDLL) -> None:
        x11.XOpenDisplay.argtypes = [c_char_p]
        x11.XOpenDisplay.restype = c_void_p
        x11.XDefaultRootWindow.argtypes = [c_void_p]
        x11.XDefaultRootWindow.restype = c_ulong
        x11.XKeysymToKeycode.argtypes = [c_void_p, c_ulong]
        x11.XKeysymToKeycode.restype = c_int
        x11.XQueryPointer.argtypes = [c_void_p, c_ulong, POINTER(c_ulong), POINTER(c_ulong),
                                      POINTER(c_int), POINTER(c_int), POINTER(c_int), POINTER(c_int), POINTER(c_uint)]
        x11.XGetInputFocus.argtypes = [c_void_p, POINTER(c_ulong), POINTER(c_int)]
        x11.XQueryKeymap.argtypes = [c_void_p, POINTER(c_ubyte)]
        x11.XGetClassHint.argtypes = [c_void_p, c_ulong, POINTER(XClassHint)]
        x11.XFetchName.argtypes = [c_void_p, c_ulong, POINTER(c_void_p)]
        x11.XQueryTree.argtypes = [c_void_p, c_ulong, POINTER(c_ulong), POINTER(c_ulong),
                                   POINTER(POINTER(c_ulong)), POINTER(c_uint)]
        x11.XFree.argtypes = [c_void_p]
        x11.XCloseDisplay.argtypes = [c_void_p]


def normalize(name: str) -> str:
    value = name.strip().strip('"').replace("\\", "/").rsplit("/", 1)[-1]
    return value[:-4].casefold() if value.casefold().endswith(".exe") else value.casefold()
