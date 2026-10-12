/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: replaces menu.c (client menu UI) on every platform. menu.c's _WIN32 code needs
WinQuake's DirectDraw video driver and SciTech headers, which the headless build does not have; the menu
never touches server state. Not registering the menu commands only means "Unknown command" for
togglemenu and friends in config files.
*/
#include "quakedef.h"

void M_Init (void) {}
void M_Keydown (int key) {}
void M_Draw (void) {}
void M_ToggleMenu_f (void) {}
void M_Menu_Main_f (void) {}
void M_Menu_Quit_f (void) {}
void M_DrawPic (int x, int y, qpic_t *pic) {}

#ifdef _WIN32
// cl_parse.c calls this under _WIN32 (vid_win.c); the null video driver has no window to pause.
void VID_HandlePause (qboolean pause) {}
#endif
