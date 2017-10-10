# What BxILMerge is for?

BxILMerge is like ILMerge to merge **unmanaged DLLs** and any other files (data files, images, videos, databases etc.) with managed DLL.

# Usage

`bxilmerge /out:<output assembly with embedded files> <input assembly> <unmanaged DLLs and other files to embed>...`

# How does it work?

BxILMerge uses Mono.Cecil to write content of each file into output assembly, adding additional code that virtualizes embedded files.

# When you need it?

 - you want to include assets into a managed assembly;
 - you want to "link" a managed assembly with unmanaged DLLs (e.g. unmanaged part of SQLite);
 - and so on.
