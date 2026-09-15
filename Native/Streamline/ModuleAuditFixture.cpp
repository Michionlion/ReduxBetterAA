// Harmless test module for checking a loaded DLL's location. It implements no
// Streamline/NGX API and is never staged with the real runtime or the mod.
extern "C" __declspec(dllexport) int RbaModuleAuditFixture() { return 1; }
