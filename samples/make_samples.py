"""Makes the invented sample claims in this folder. Every patient, member, provider, NPI and tax ID here is made up.

Each claim in clinic-837p.x12 and hospital-837i.x12 is either clean or has one planted problem, named in its CLM01
(the patient account number), so the tests and the screenshots can point at them. envelope-errors.x12 has its counts
and control numbers wrong on purpose.

Run: python3 make_samples.py
"""


def npi(nine):
    """A ten-digit NPI from nine digits: the Luhn check digit over the prefix 80840."""
    digits = "80840" + nine
    total = 0
    for i, ch in enumerate(reversed(digits)):
        d = int(ch)
        if i % 2 == 0:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    return nine + str((10 - total % 10) % 10)


BILLING_NPI = npi("123456789")
RENDER_NPI = npi("198765432")
FACILITY_NPI = npi("154321098")
BAD_NPI = BILLING_NPI[:-1] + str((int(BILLING_NPI[-1]) + 1) % 10)
CREATED = "20261115"   # BHT04: the day the batch was made


def isa(control, date="261115", time="0930"):
    return ("ISA*00*          *00*          *ZZ*RIVERBENDCLN   *ZZ*CLEARHOUSE     *"
            f"{date}*{time}*^*00501*{control:09d}*0*T*:")


class Batch:
    def __init__(self, version, sender="RIVERBENDCLN"):
        self.version = version
        self.sender = sender
        self.body = []

    def seg(self, *els):
        self.body.append("*".join(els))

    def text(self, control=1, group=1, st="0001", wrong=None):
        wrong = wrong or {}
        st_segs = [f"ST*837*{st}*{self.version}"] + self.body
        count = len(st_segs) + 1 + wrong.get("se_extra", 0)
        segs = [isa(control), f"GS*HC*{self.sender}*CLEARHOUSE*{CREATED}*0930*{group}*X*{self.version}"]
        segs += st_segs + [f"SE*{count}*{st}", f"GE*{1 + wrong.get('ge_extra', 0)}*{group}",
                           f"IEA*1*{control + wrong.get('iea_off', 0):09d}"]
        return "~\n".join(segs) + "~\n"


def header(b, kind):
    b.seg("BHT", "0019", "00", f"BATCH{kind}01", CREATED, "0930", "CH")
    b.seg("NM1", "41", "2", "RIVERBEND FAMILY CLINIC", "", "", "", "", "46", "RIVERBEND01")
    b.seg("PER", "IC", "BILLING OFFICE", "TE", "5555550100")
    b.seg("NM1", "40", "2", "CLEARHOUSE", "", "", "", "", "46", "CLEARHOUSE01")


def billing(b, hl, name="RIVERBEND FAMILY CLINIC", npi_=BILLING_NPI, taxonomy="207Q00000X"):
    b.seg("HL", str(hl), "", "20", "1")
    b.seg("PRV", "BI", "PXC", taxonomy)
    b.seg("NM1", "85", "2", name, "", "", "", "", "XX", npi_)
    b.seg("N3", "100 RIVER ROAD")
    b.seg("N4", "SPRINGFIELD", "IL", "627010001")
    b.seg("REF", "EI", "371234567")


def subscriber(b, hl, parent, member, last, first, birth, sex, payer="SUNPLAN", payer_id="SUN01", rel="18"):
    b.seg("HL", str(hl), str(parent), "22", "0")
    b.seg("SBR", "P", rel, "GRP100", "", "", "", "", "", "CI")
    b.seg("NM1", "IL", "1", last, first, "", "", "", "MI", member)
    b.seg("N3", "12 ELM STREET")
    b.seg("N4", "SPRINGFIELD", "IL", "62704")
    b.seg("DMG", "D8", birth, sex)
    b.seg("NM1", "PR", "2", payer, "", "", "", "", "PI", payer_id)


def claim_p(b, account, lines, dx, pos="11", total=None, freq="1", extra=None, render=RENDER_NPI, taxonomy="207Q00000X"):
    """A professional claim (2300 and its 2400 lines). lines: (code, modifiers, charge, units, date, pointers)."""
    total = total if total is not None else sum(l[2] for l in lines)
    b.seg("CLM", account, f"{total:.2f}", "", "", f"{pos}:B:{freq}", "Y", "A", "Y", "Y")
    for e in extra or []:
        b.seg(*e)
    b.seg("HI", *[("ABK" if i == 0 else "ABF") + ":" + d for i, d in enumerate(dx)])
    if render:
        b.seg("NM1", "82", "1", "OKAFOR", "LINDA", "", "", "", "XX", render)
        b.seg("PRV", "PE", "PXC", taxonomy)
    for n, (code, mods, charge, units, date, pointers) in enumerate(lines, 1):
        b.seg("LX", str(n))
        b.seg("SV1", ":".join(["HC", code] + mods), f"{charge:.2f}", "UN", str(units), "", "", pointers)
        b.seg("DTP", "472", "D8", date)


def make_837p():
    b = Batch("005010X222A1")
    header(b, "P")
    billing(b, 1)
    hl = 2
    planted = [
        # account, member, last, first, birth, sex, lines, dx, extra args
        ("CLEAN-01", "SUN100001", "MARTINEZ", "ELENA", "19700312", "F",
         [("99213", [], 125.00, 1, "20261103", "1"), ("36415", [], 25.00, 1, "20261103", "1")], ["I10"], {}),
        ("TOTAL-02", "SUN100002", "BROOKS", "DANIEL", "19651120", "M",
         [("99214", [], 175.00, 1, "20261104", "1"), ("82947", [], 50.00, 1, "20261104", "1")], ["E119"], {"total": 250.00}),
        ("FUTURE-03", "SUN100003", "NGUYEN", "LAN", "19880205", "F",
         [("99213", [], 125.00, 1, "20261212", "1")], ["J45909"], {}),
        ("DOT-04", "SUN100004", "PATEL", "ARJUN", "19920830", "M",
         [("90837", [], 180.00, 1, "20261105", "1")], ["F32.9"], {}),
        ("UNITS-05", "SUN100005", "OKONKWO", "GRACE", "19590417", "F",
         [("99213", [], 125.00, 0, "20261106", "1")], ["Z0000"], {}),
        ("REPLACE-06", "SUN100006", "FISCHER", "TOM", "19771009", "M",
         [("99213", [], 125.00, 1, "20261020", "1")], ["I10"], {"freq": "7"}),
        ("BADDATE-07", "SUN100007", "SILVA", "ROSA", "19810101", "F",
         [("99213", [], 125.00, 1, "20261131", "1")], ["I10"], {}),
    ]
    for account, member, last, first, birth, sex, lines, dx, kw in planted:
        subscriber(b, hl, 1, member, last, first, birth, sex)
        hl += 1
        claim_p(b, account, lines, dx, **kw)
    return b


def make_837i():
    b = Batch("005010X223A2", sender="RIVERBENDHSP")
    header(b, "I")
    billing(b, 1, name="RIVERBEND COMMUNITY HOSPITAL", npi_=FACILITY_NPI, taxonomy="282N00000X")
    hl = 2

    def claim_i(account, member, last, first, birth, sex, period, lines, dx, bill="111", total=None):
        nonlocal hl
        subscriber(b, hl, 1, member, last, first, birth, sex)
        hl += 1
        total = total if total is not None else sum(l[2] for l in lines)
        b.seg("CLM", account, f"{total:.2f}", "", "", f"{bill[:2]}:A:{bill[2]}", "", "A", "Y", "Y")
        b.seg("DTP", "434", "RD8", period)
        b.seg("CL1", "1", "7", "01")
        b.seg("HI", *[("ABK" if i == 0 else "ABF") + ":" + d for i, d in enumerate(dx)])
        b.seg("NM1", "71", "1", "HARGROVE", "PETER", "", "", "", "XX", RENDER_NPI)
        for n, (rev, code, charge, units, date) in enumerate(lines, 1):
            b.seg("LX", str(n))
            b.seg("SV2", rev, f"HC:{code}" if code else "", f"{charge:.2f}", "UN", str(units))
            b.seg("DTP", "472", "D8", date)

    claim_i("INPT-CLEAN-01", "SUN200001", "ABERNATHY", "JOAN", "19500704", "F", "20261101-20261104",
            [("0120", "", 3600.00, 3, "20261101"), ("0300", "80053", 140.00, 1, "20261102")], ["I214", "I10"])
    claim_i("INPT-OUTSIDE-02", "SUN200002", "KOWALSKI", "PIOTR", "19480219", "M", "20261105-20261107",
            [("0120", "", 2400.00, 2, "20261105"), ("0250", "", 85.00, 1, "20261109")], ["J189"])
    return b


def make_envelope_errors():
    b = Batch("005010X222A1")
    header(b, "E")
    billing(b, 1)
    subscriber(b, 2, 1, "SUN300001", "LARSEN", "ERIK", "19730611", "M")
    claim_p(b, "ENV-01", [("99213", [], 125.00, 1, "20261103", "1")], ["I10"])
    return b.text(control=7, wrong={"se_extra": 1, "ge_extra": 1, "iea_off": 1})


def make_structure_errors():
    """Structure problems (layer 2), one a claim."""
    b = Batch("005010X222A1")
    header(b, "S")
    billing(b, 1)
    # A pointer to a third diagnosis when the claim has one.
    subscriber(b, 2, 1, "SUN400001", "QUINN", "MARA", "19840822", "F")
    claim_p(b, "POINTER-01", [("99213", [], 125.00, 1, "20261103", "1:3")], ["I10"])
    # Lines numbered 1 and 3.
    subscriber(b, 3, 1, "SUN400002", "DUNN", "OWEN", "19790314", "M")
    claim_p(b, "LXGAP-02", [("99213", [], 125.00, 1, "20261103", "1"), ("36415", [], 25.00, 1, "20261103", "1")], ["I10"])
    i = max(k for k, seg in enumerate(b.body) if seg == "LX*2")
    b.body[i] = "LX*3"
    # No diagnosis at all.
    subscriber(b, 4, 1, "SUN400003", "ROWE", "TESS", "19900101", "F")
    claim_p(b, "NODX-03", [("99213", [], 125.00, 1, "20261103", "1")], ["I10"])
    i = max(k for k, seg in enumerate(b.body) if seg.startswith("HI*"))
    del b.body[i]
    # A subscriber whose HL names a parent that isn't there.
    subscriber(b, 5, 9, "SUN400004", "VOSS", "IDA", "19660707", "F")
    claim_p(b, "PARENT-04", [("99213", [], 125.00, 1, "20261103", "1")], ["I10"])
    # A subscriber with no payer.
    subscriber(b, 6, 1, "SUN400005", "YATES", "LEO", "19750505", "M")
    i = max(k for k, seg in enumerate(b.body) if seg.startswith("NM1*PR"))
    del b.body[i]
    claim_p(b, "NOPAYER-05", [("99213", [], 125.00, 1, "20261103", "1")], ["I10"])
    return b.text(control=9)


def make_coding_errors():
    """Code-set problems (layer 4), one a claim, and a clean claim that uses modifiers and a HCPCS code."""
    b = Batch("005010X222A1")
    header(b, "C")
    billing(b, 1)
    claims = [
        ("CODES-CLEAN-01", [("99213", ["25"], 125.00, 1, "20261103", "1"), ("G0439", [], 175.00, 1, "20261103", "1")], ["I10"], {}),
        ("HEADER-02", [("99213", [], 125.00, 1, "20261103", "1")], ["E11"], {}),
        ("NOTACODE-03", [("99213", [], 125.00, 1, "20261103", "1")], ["E1199"], {}),
        ("NPI-04", [("99213", [], 125.00, 1, "20261103", "1")], ["I10"], {"render": BAD_NPI}),
        ("ENDED-05", [("G9975", [], 10.00, 1, "20261103", "1")], ["I10"], {}),
        ("MODIFIER-06", [("99213", ["X9"], 125.00, 1, "20261103", "1")], ["I10"], {}),
        ("POS-07", [("99213", [], 125.00, 1, "20261103", "1")], ["I10"], {"pos": "30"}),
        ("TAXONOMY-08", [("99213", [], 125.00, 1, "20261103", "1")], ["I10"], {"taxonomy": "207Q0000"}),
        ("CPT-09", [("9921", [], 125.00, 1, "20261103", "1")], ["I10"], {}),
    ]
    for n, (account, lines, dx, kw) in enumerate(claims):
        subscriber(b, n + 2, 1, f"SUN5{n:05d}", "MORENO", "ALICE", "19800101", "F")
        claim_p(b, account, lines, dx, **kw)
    return b.text(control=11)


def make_edits():
    """NCCI, MUE and age or sex problems (layer 5). The NCCI pairs the tests use are invented (CMS's PTP files are
    licensed from the AMA); the MUE for G0439 is CMS's (1 a day)."""
    b = Batch("005010X222A1")
    header(b, "N")
    billing(b, 1)
    claims = [
        ("NCCI-01", "F", "19700101", [("G0438", [], 175.00, 1, "20261103", "1"), ("G0439", [], 175.00, 1, "20261103", "1")], ["Z0000"]),
        ("NCCI-OK-02", "F", "19700101", [("99213", [], 125.00, 1, "20261103", "1"), ("J1100", ["59"], 15.00, 1, "20261103", "1")], ["J45909"]),
        ("NCCI-03", "F", "19700101", [("99213", [], 125.00, 1, "20261103", "1"), ("J1100", [], 15.00, 1, "20261103", "1")], ["J45909"]),
        ("MUE-04", "F", "19700101", [("G0439", [], 350.00, 2, "20261103", "1")], ["Z0000"]),
        ("SEX-05", "M", "19860101", [("99213", [], 125.00, 1, "20261103", "1")], ["O80"]),
        ("AGE-06", "F", "19860101", [("99213", [], 125.00, 1, "20261103", "1")], ["P0700"]),
    ]
    for n, (account, sex, birth, lines, dx) in enumerate(claims):
        subscriber(b, n + 2, 1, f"SUN6{n:05d}", "HALE", "SAM", birth, sex)
        claim_p(b, account, lines, dx)
    return b.text(control=13)


def make_payer():
    """Sun Plan's payer rules (layer 6): timely filing, prior authorization, telehealth modifiers."""
    b = Batch("005010X222A1")
    header(b, "R")
    billing(b, 1)
    claims = [
        ("LATE-01", [("99213", [], 125.00, 1, "20260701", "1")], {}),
        ("AUTH-02", [("90837", [], 180.00, 1, "20261103", "1")], {}),
        ("AUTH-OK-03", [("90837", [], 180.00, 1, "20261103", "1")], {"extra": [("REF", "G1", "AUTH0042")]}),
        ("TELE-04", [("99213", [], 125.00, 1, "20261103", "1")], {"pos": "10"}),
        ("TELE-OK-05", [("99213", ["95"], 125.00, 1, "20261103", "1")], {"pos": "10"}),
    ]
    for n, (account, lines, kw) in enumerate(claims):
        subscriber(b, n + 2, 1, f"SUN7{n:05d}", "PRICE", "JO", "19750101", "F")
        claim_p(b, account, lines, ["F329"], **kw)
    return b.text(control=15)


if __name__ == "__main__":
    with open("clinic-837p.x12", "w", newline="") as f:
        f.write(make_837p().text(control=1))
    with open("hospital-837i.x12", "w", newline="") as f:
        f.write(make_837i().text(control=2, group=2))
    with open("envelope-errors.x12", "w", newline="") as f:
        f.write(make_envelope_errors())
    with open("structure-errors.x12", "w", newline="") as f:
        f.write(make_structure_errors())
    with open("coding-errors.x12", "w", newline="") as f:
        f.write(make_coding_errors())
    with open("edits-837p.x12", "w", newline="") as f:
        f.write(make_edits())
    with open("payer-837p.x12", "w", newline="") as f:
        f.write(make_payer())
    print("billing NPI", BILLING_NPI, "rendering", RENDER_NPI, "facility", FACILITY_NPI)
