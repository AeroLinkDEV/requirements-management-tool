# Out-the-window helicopter model

`helicopter-light-twin.glb` is the chase view's helicopter.

- **What it is:** an original, generic model of a light twin-engine helicopter in the class of the Airbus H135:
  - a main rotor of about 10.2 m;
  - a shrouded tail fan;
  - skids;
  - four main blades.
- **How it was made:** generated from simple solids by `src/fmsCdu/helicopterModel.ts`, and written by `scripts/build-helicopter-model.mjs`. `tests/fms-helicopter-model.spec.ts` checks that this file is exactly what the generator writes, so change the generator, never the file.
- **Why an original:** Sean decided on 29 September 2026 that the bench builds its own model. No open-licence (CC0 or CC-BY) Airbus Helicopters model could be downloaded without signing in, and the bench does not create accounts or sign in anywhere.
- **Licence:** part of the AeroLink project, like the rest of the repository. No third-party geometry, textures or images.
- **Trademarks:** none. It is not an Airbus Helicopters asset or a model of a real aircraft's exact shape, and has no livery or logos.
- **Axes:** +x forward, +y left, +z up, in metres, with the origin at the main rotor mast's foot. The scene loads it with those axes declared (`otwAircraftModel.ts`). The main rotor (`main_rotor`, turning about +z) and the tail fan (`tail_rotor`, turning about +y) are separate nodes.
