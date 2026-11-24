Small excerpts of the public code sets, so the tests run without downloading anything:

- icd10cm_order_extract.txt: lines of CMS's ICD-10-CM FY2027 order file (icd10cm_order_2027.txt), unchanged.
- hcpcs_extract.txt: lines of CMS's October 2026 HCPCS alpha-numeric file (HCPC2026_OCT_ANWEB), unchanged.
- taxonomy_extract.csv: rows of NUCC's Health Care Provider Taxonomy, version 26.1, unchanged.

The full files are downloaded by the program's Update button (Code Sets tab), from cms.gov and nucc.org.
- mue_practitioner_extract.csv: rows of CMS's practitioner MUE table effective October 1, 2026, HCPCS Level II codes
  only, unchanged.
- ncci_ptp_invented.txt: INVENTED edits in the layout of CMS's NCCI PTP files. The real files are behind the AMA's
  licence page on cms.gov, so none of them is here; the program imports them once a person has accepted it.
