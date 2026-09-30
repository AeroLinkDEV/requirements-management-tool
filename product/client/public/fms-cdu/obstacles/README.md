# FAA Digital Obstacle File: bench extract

`dof-bench-extract.csv` holds the obstacles the FMS Test Bench's out-the-window view draws near the bench's US areas: KBTV, 87N, KJRA, KJFK, KLGA and 2P2, within 40 NM of each.

- **Source:** the FAA Digital Obstacle File, daily CSV, <https://aeronav.faa.gov/Obst_Data/DAILY_DOF_CSV.ZIP>. It was downloaded once on 29 September 2026 with Sean's authorisation, and is not refreshed daily.
  - The zip is 20,625,893 bytes, SHA-256 `c88d4c33792c649342ab3700346e28f72dbb4d7d1ec06f82924fc1135164bafe`.
  - It contains `DOF.CSV`: 99,353,808 bytes, dated 28 September 2026, 656,855 records, SHA-256 `db32b542be2d40795b6ed01df2fbb6947c67d6fce88676fc03d82a2476f6d689`.
  - Neither the zip nor the full CSV is in the repository.
- **The extract:** the CSV header and the 11,973 records whose position (LATDEC, LONDEC) lies within 40 NM of one of the six reference points. Records are unchanged apart from line endings (LF). SHA-256 `ff1ff3950f1972fd66ed17e7b539efc37f52f9801241dee333886ca4c215ca8a`.
- **Licence:** a US Government work, in the public domain.
- **For demonstration only, not for navigation.** The DOF is updated daily; this extract is a single snapshot.
- **Fields used** (`obstacles.ts`): OAS number, VERIFIED STATUS, LATDEC and LONDEC, TYPE, QUANTITY, AGL and AMSL heights in feet, LIGHTING, ACCURACY and MARKING, as the FAA publishes them. The accuracy and lighting codes are kept as published and are not decoded.
