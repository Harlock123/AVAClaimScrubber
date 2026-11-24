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

    def text(self, control=1, group=None, st="0001", wrong=None):
        wrong = wrong or {}
        group = control if group is None else group   # each file's GS06 is its own, so a 999 can name it
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


def envelope(kind, version, control, body, sender="SUNPLAN", receiver="RIVERBENDCLN", functional="FA", date="20261118"):
    """A response file: ISA, GS, one transaction of body segments, SE, GE, IEA. The payer sends it to the clinic."""
    segs = [("ISA*00*          *00*          *ZZ*" + f"{sender:<15}" + "*ZZ*" + f"{receiver:<15}" + "*" + date[2:] +
             f"*1200*^*00501*{control:09d}*0*T*:"),
            f"GS*{functional}*{sender}*{receiver}*{date}*1200*{control}*X*{version}", f"ST*{kind}*0001*{version}"]
    segs += body
    segs += [f"SE*{len(body) + 2}*0001", f"GE*1*{control}", f"IEA*1*{control:09d}"]
    return "~\n".join(segs) + "~\n"


def segments_of(text):
    """The segments of an 837 the way a 999 counts them: from ST (position 1)."""
    segs = [s.strip() for s in text.split("~") if s.strip()]
    st = next(i for i, s in enumerate(segs) if s.startswith("ST*"))
    return segs, st


def make_structure_999(structure_text):
    """A 999 rejecting structure-errors.x12: the HL with a parent that isn't there, and the subscriber with no payer."""
    segs, st = segments_of(structure_text)
    gs06 = next(s for s in segs if s.startswith("GS*")).split("*")[6]
    parent = next(i for i, s in enumerate(segs) if s.startswith("HL*") and s.split("*")[2] == "9")
    nopayer_clm = next(i for i, s in enumerate(segs) if s.startswith("CLM*NOPAYER-05"))
    nopayer_hl = max(i for i in range(nopayer_clm) if segs[i].startswith("HL*"))
    body = [f"AK1*HC*{gs06}*005010X222A1", "AK2*837*0001*005010X222A1",
            f"IK3*HL*{parent - st + 1}**8", "IK4*2*734*7",
            f"IK3*NM1*{nopayer_hl - st + 1}*2010BB*3",
            "IK5*R*5", "AK9*R*1*1*0"]
    return envelope("999", "005010X231A1", 21, body, sender="CLEARHOUSE")


def make_envelope_999(envelope_text):
    """A 999 rejecting envelope-errors.x12: its SE01 is wrong."""
    segs, _ = segments_of(envelope_text)
    gs06 = next(s for s in segs if s.startswith("GS*")).split("*")[6]
    body = [f"AK1*HC*{gs06}*005010X222A1", "AK2*837*0001*005010X222A1", "IK5*R*4", "AK9*R*1*1*0"]
    return envelope("999", "005010X231A1", 22, body, sender="CLEARHOUSE")


def make_clinic_277ca():
    """The payer's 277CA for clinic-837p.x12: a status for each claim."""
    statuses = [
        ("CLEAN-01", "MARTINEZ", "ELENA", "SUN100001", "A2:20", 150.00, "20261103"),
        ("TOTAL-02", "BROOKS", "DANIEL", "SUN100002", "A7:400", 250.00, "20261104"),
        ("FUTURE-03", "NGUYEN", "LAN", "SUN100003", "A7:510", 125.00, "20261212"),
        ("DOT-04", "PATEL", "ARJUN", "SUN100004", "A7:255", 180.00, "20261105"),
        ("UNITS-05", "OKONKWO", "GRACE", "SUN100005", "A2:20", 125.00, "20261106"),
        ("REPLACE-06", "FISCHER", "TOM", "SUN100006", "A6:33", 125.00, "20261020"),
        ("BADDATE-07", "SILVA", "ROSA", "SUN100007", "A7:187", 125.00, "20261131"),
    ]
    body = ["BHT*0085*08*277CA0001*20261117*1200*TH",
            "HL*1**20*1", "NM1*PR*2*SUNPLAN*****PI*SUN01", "TRN*1*277CA0001", "DTP*050*D8*20261116", "DTP*009*D8*20261117",
            "HL*2*1*21*1", "NM1*41*2*RIVERBEND FAMILY CLINIC*****46*RIVERBEND01", "TRN*2*BATCHP01",
            f"STC*A1:19:PR*20261116*WQ*{sum(s[5] for s in statuses):.2f}",
            "QTY*90*3", "QTY*AA*4", f"AMT*YU*{sum(s[5] for s in statuses if s[4].startswith('A2')):.2f}",
            f"AMT*YY*{sum(s[5] for s in statuses if not s[4].startswith('A2')):.2f}",
            "HL*3*2*19*1", f"NM1*85*2*RIVERBEND FAMILY CLINIC*****XX*{BILLING_NPI}", "TRN*1*0",
            "QTY*QA*3", "QTY*QC*4"]
    hl = 4
    for i, (account, last, first, member, stc, amount, date) in enumerate(statuses, 1):
        action = "WQ" if stc.startswith("A2") else "U"
        body += [f"HL*{hl}*3*PT", f"NM1*QC*1*{last}*{first}****MI*{member}", f"TRN*2*{account}",
                 f"STC*{stc}*20261117*{action}*{amount:.2f}", f"REF*1K*SP277{i:04d}", f"DTP*472*D8*{date}"]
        hl += 1
    return envelope("277", "005010X214", 23, body, functional="HN")


def make_sunplan_835():
    """Sun Plan's remittance for payer-837p.x12, edits-837p.x12 and one claim of coding-errors.x12."""
    claims = [
        # account, status, last, first, member, lines: (code, mods, charge, paid, units, original units, date, [(group, reason, amount)], remarks)
        ("LATE-01", "4", "PRICE", "JO", "SUN700000", [("99213", [], 125.00, 0, 1, None, "20260701", [("CO", "29", 125.00)], [])]),
        ("AUTH-02", "4", "PRICE", "JO", "SUN700001", [("90837", [], 180.00, 0, 1, None, "20261103", [("CO", "197", 180.00)], ["N54"])]),
        ("AUTH-OK-03", "1", "PRICE", "JO", "SUN700002", [("90837", [], 180.00, 144.00, 1, None, "20261103", [("CO", "45", 36.00)], [])]),
        ("TELE-04", "4", "PRICE", "JO", "SUN700003", [("99213", [], 125.00, 0, 1, None, "20261103", [("CO", "4", 125.00)], [])]),
        ("TELE-OK-05", "1", "PRICE", "JO", "SUN700004", [("99213", ["95"], 125.00, 80.00, 1, None, "20261103", [("CO", "45", 25.00), ("PR", "3", 20.00)], [])]),
        ("NCCI-OK-02", "1", "HALE", "SAM", "SUN600001", [("99213", [], 125.00, 100.00, 1, None, "20261103", [("CO", "45", 25.00)], []),
                                                         ("J1100", ["59"], 15.00, 12.00, 1, None, "20261103", [("CO", "45", 3.00)], [])]),
        ("NCCI-03", "1", "HALE", "SAM", "SUN600002", [("99213", [], 125.00, 100.00, 1, None, "20261103", [("CO", "45", 25.00)], []),
                                                      ("J1100", [], 15.00, 0, 1, None, "20261103", [("CO", "97", 15.00)], [])]),
        ("MUE-04", "1", "HALE", "SAM", "SUN600003", [("G0439", [], 350.00, 175.00, 1, 2, "20261103", [("CO", "151", 175.00)], ["N362"])]),
        ("SEX-05", "4", "HALE", "SAM", "SUN600004", [("99213", [], 125.00, 0, 1, None, "20261103", [("CO", "10", 125.00)], [])]),
        ("CODES-CLEAN-01", "4", "MORENO", "ALICE", "SUN500000", [("99213", ["25"], 125.00, 0, 1, None, "20261103", [("CO", "31", 125.00)], []),
                                                                 ("G0439", [], 175.00, 0, 1, None, "20261103", [("CO", "31", 175.00)], [])]),
    ]
    paid_total = sum(l[3] for c in claims for l in c[5])
    body = [f"BPR*I*{paid_total:.2f}*C*ACH*CCP*01*999999992*DA*123456*1512345678**01*999988880*DA*98765*20261120",
            "TRN*1*EFT20261120*1512345678", "DTM*405*20261120",
            "N1*PR*SUNPLAN", "N3*1 SUN PLAZA", "N4*SPRINGFIELD*IL*62701", "REF*2U*SUN01", "PER*BL*PROVIDER SERVICES*TE*5555550199",
            f"N1*PE*RIVERBEND FAMILY CLINIC*XX*{BILLING_NPI}", "REF*TJ*371234567", "LX*1"]
    for i, (account, status, last, first, member, lines) in enumerate(claims, 1):
        charge = sum(l[2] for l in lines)
        paid = sum(l[3] for l in lines)
        patient = sum(a[2] for l in lines for a in l[7] if a[0] == "PR")
        body += [f"CLP*{account}*{status}*{charge:.2f}*{paid:.2f}*{patient:.2f}*12*SP835{i:04d}*11*1",
                 f"NM1*QC*1*{last}*{first}****MI*{member}"]
        for code, mods, ch, pd, units, original, date, adjustments, remarks in lines:
            svc = f"SVC*{':'.join(['HC', code] + mods)}*{ch:.2f}*{pd:.2f}**{units}"
            if original is not None:
                svc += f"**{original}"
            body += [svc, f"DTM*472*{date}"]
            for group in dict.fromkeys(a[0] for a in adjustments):
                parts = [f"{a[1]}*{a[2]:.2f}" for a in adjustments if a[0] == group]
                body.append(f"CAS*{group}*" + "**".join(parts))
            body += [f"LQ*HE*{r}" for r in remarks]
    return envelope("835", "005010X221A1", 24, body, functional="HP", date="20261120")


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
    import os
    os.makedirs("responses", exist_ok=True)
    with open("structure-errors.x12") as f:
        structure = f.read()
    with open("envelope-errors.x12") as f:
        env = f.read()
    for name, text in [("structure-999.x12", make_structure_999(structure)), ("envelope-999.x12", make_envelope_999(env)),
                       ("clinic-277ca.x12", make_clinic_277ca()), ("sunplan-835.x12", make_sunplan_835())]:
        with open(os.path.join("responses", name), "w", newline="") as f:
            f.write(text)
    print("billing NPI", BILLING_NPI, "rendering", RENDER_NPI, "facility", FACILITY_NPI)
