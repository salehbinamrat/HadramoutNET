# Restore the recovered v1.1.3 source

**Target:** private repository `salehbinzain83-star/Hadramout-cards`, branch `recovery/v1.1.3-x64`.

1. Upload **exactly** `Hadramout_v1.1.3_recovered_sources.zip` to the **root of this branch** using GitHub's Add file > Upload files (not the main branch). Expected ZIP SHA-256:
   `c3d6bb50943882aacd06c544b0ffa9229741f6648f4057c11a3a40fa978003f8`.
2. The workflow `Hadramout recovery and Windows gates` validates this exact archive, recovers the 31 Python sources and 17 image assets, removes the uploaded ZIP from the branch's current tree, then starts the Windows Core and Windows Release checks concurrently.
3. The release tests run **before** any packaging. Final packaging remains blocked until a real, tested Windows build script is restored or implemented; this archive does not provide the original installer project or verified x86 build chain.
4. **Do not merge** recovery into `main` until tests pass and all four real packages (x86/x64 Portable and Installer) are built and SHA-256-verified.

The release ZIP will remain in Git history even after its tracked file is removed; this is a **private** repository. The recovery is not the original Git history. Do not insert licensing private keys or customer information.
