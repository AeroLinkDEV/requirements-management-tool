"""CMA-9000 CDU faceplate geometry, in millimetres, origin at the faceplate centre, x right, y up.

Sources: CMC datasheet CMC-CMA9000-FMS-RMS-19-003 (public release) for the outline, fastener spacing and
display, and the CMA-9000 Operator's Manual Figures 2-1 to 2-9 for element positions, measured and scaled
against the datasheet width. The keys, annunciator windows and screen are identical across hardware
variations; only legends differ, and those are data (variants.json), not geometry.
"""

PANEL_W = 146.05          # 5.75 in
PANEL_H = 171.45          # 6.75 in
SCREW_DX = 136.27 / 2     # 5.365 in between fastener centres
SCREW_DY = 161.93 / 2     # 6.375 in

# Display: 14 lines x 24 characters. The line pitch is half the LSK pitch, so each LSK sits beside a data line.
LSK_PITCH = 9.95
LINE_PITCH = LSK_PITCH / 2
SCREEN_CY = 27.7
SCREEN_ACTIVE_H = 14 * LINE_PITCH
SCREEN_ACTIVE_W = 94.0
SCREEN_GLASS_W, SCREEN_GLASS_H = 99.0, 76.5
SCREEN_BEZEL_W, SCREEN_BEZEL_H = 104.0, 81.0

LSK_W, LSK_H = 8.6, 5.4
LSK_X = 66.2
# LSK n (0..5) aligns with display line 2 + 2n (0-based; line 0 is the title, line 13 the scratchpad).
SCREEN_TOP = SCREEN_CY + SCREEN_ACTIVE_H / 2


def line_centre_y(line):
    return SCREEN_TOP - (line + 0.5) * LINE_PITCH


LSK_Y = [line_centre_y(2 + 2 * n) for n in range(6)]

ANNUNCIATOR_Y = 77.8
ANNUNCIATOR_X = [-49.8 + i * 16.6 for i in range(7)]
ANNUNCIATOR_W, ANNUNCIATOR_H = 11.6, 6.4
SIDE_ANNUNCIATOR_X = 68.0   # MENU (left) and EXEC (right) indicator pills beside function row 1
SIDE_ANNUNCIATOR_W, SIDE_ANNUNCIATOR_H = 2.2, 7.4
LDR_X, LDR_Y, LDR_D = 66.6, 64.1, 3.0

ROW_Y = [-19.1, -31.2, -43.2, -54.7, -65.7, -76.7]
NUM_X = [-60.2, -47.6, -34.6]
WIDE_X = [-16.8, -2.1, 13.1, 27.8, 42.9, 58.1]   # function keys INIT/REF .. EXEC, and row 2
ALPHA_X = [-17.8 + i * 12.83 for i in range(7)]

FUNC_SMALL = (11.4, 8.0)   # MENU PREV NEXT
FUNC_WIDE = (13.4, 8.0)
ALPHA = (10.8, 9.0)
ROUND_D = 9.4
SMALL_RECT = (9.6, 7.6)    # / and SP

# Key ids are stable across variants; variants.json maps each id to its legend for a hardware variation.
# kind: rect | round. role: line-select | function | numeric | alpha
def keys():
    out = []
    for n, y in enumerate(LSK_Y):
        out.append(dict(id=f"LSK{n + 1}L", x=-LSK_X, y=y, w=LSK_W, h=LSK_H, kind="lsk", role="line-select"))
        out.append(dict(id=f"LSK{n + 1}R", x=LSK_X, y=y, w=LSK_W, h=LSK_H, kind="lsk", role="line-select"))
    for i, key in enumerate(["MENU", "PREV", "NEXT"]):
        out.append(dict(id=key, x=NUM_X[i], y=ROW_Y[0], w=FUNC_SMALL[0], h=FUNC_SMALL[1], kind="rect", role="function"))
    for i, key in enumerate(["INIT_REF", "RTE", "DEP_ARR", "LEGS", "PROG", "EXEC"]):
        out.append(dict(id=key, x=WIDE_X[i], y=ROW_Y[0], w=FUNC_WIDE[0], h=FUNC_WIDE[1], kind="rect", role="function"))
    for i in range(6):
        out.append(dict(id=f"F2_{i + 1}", x=WIDE_X[i], y=ROW_Y[1], w=FUNC_WIDE[0], h=FUNC_WIDE[1], kind="rect", role="function"))
    numeric = [["1", "2", "3"], ["4", "5", "6"], ["7", "8", "9"], ["DOT", "0", "PLUSMINUS"]]
    for r, row in enumerate(numeric):
        for c, key in enumerate(row):
            out.append(dict(id=key, x=NUM_X[c], y=ROW_Y[1 + r], w=ROUND_D, h=ROUND_D, kind="round", role="numeric"))
    out.append(dict(id="SLASH", x=NUM_X[1], y=ROW_Y[5], w=SMALL_RECT[0], h=SMALL_RECT[1], kind="rect", role="numeric"))
    out.append(dict(id="SP", x=NUM_X[2], y=ROW_Y[5], w=SMALL_RECT[0], h=SMALL_RECT[1], kind="rect", role="numeric"))
    alpha_rows = ["ABCDEFG", "HIJKLMN", "OPQRSTU"]
    for r, letters in enumerate(alpha_rows):
        for c, letter in enumerate(letters):
            out.append(dict(id=letter, x=ALPHA_X[c], y=ROW_Y[2 + r], w=ALPHA[0], h=ALPHA[1], kind="rect", role="alpha"))
    for c, key in enumerate(["V", "W", "X", "Y", "Z", "CLR"]):
        out.append(dict(id=key, x=ALPHA_X[c], y=ROW_Y[5], w=ALPHA[0], h=ALPHA[1], kind="rect", role="alpha"))
    return out


def annunciators():
    out = [dict(id=f"A{i + 1}", x=x, y=ANNUNCIATOR_Y, w=ANNUNCIATOR_W, h=ANNUNCIATOR_H) for i, x in enumerate(ANNUNCIATOR_X)]
    out.append(dict(id="MENU_LIGHT", x=-SIDE_ANNUNCIATOR_X, y=ROW_Y[0], w=SIDE_ANNUNCIATOR_W, h=SIDE_ANNUNCIATOR_H))
    out.append(dict(id="EXEC_LIGHT", x=SIDE_ANNUNCIATOR_X, y=ROW_Y[0], w=SIDE_ANNUNCIATOR_W, h=SIDE_ANNUNCIATOR_H))
    return out


def screws():
    return [(sx * SCREW_DX, sy * SCREW_DY) for sx in (-1, 1) for sy in (-1, 1)]
