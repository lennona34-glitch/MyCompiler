// Exports every function in the current program as decompiled C into one file.
// Used by MyCompiler: analyzeHeadless ... -postScript MyCompilerExportC.java <output.c>
// @category MyCompiler
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;

import java.io.File;
import java.io.PrintWriter;

public class MyCompilerExportC extends GhidraScript {
    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        File output = new File(args.length > 0 ? args[0] : currentProgram.getName() + ".c");
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);

        int total = currentProgram.getFunctionManager().getFunctionCount();
        int done = 0, failed = 0;
        try (PrintWriter out = new PrintWriter(output, "UTF-8")) {
            out.println("// Decompiled by Ghidra via MyCompiler");
            out.println("// Program: " + currentProgram.getName() + "  ·  " + currentProgram.getLanguageID());
            out.println("// Functions: " + total);
            out.println("// Note: native decompilation is an approximation, not the original source.");
            out.println();
            FunctionIterator functions = currentProgram.getFunctionManager().getFunctions(true);
            while (functions.hasNext() && !monitor.isCancelled()) {
                Function function = functions.next();
                if (function.isThunk() || function.isExternal()) continue;
                DecompileResults result = decompiler.decompileFunction(function, 60, monitor);
                if (result != null && result.decompileCompleted()) {
                    out.println("// " + function.getEntryPoint() + "  " + function.getName());
                    out.println(result.getDecompiledFunction().getC());
                } else {
                    failed++;
                    out.println("// " + function.getEntryPoint() + "  " + function.getName() + " : decompile failed");
                    out.println();
                }
                done++;
                if (done % 250 == 0) println("MyCompiler: " + done + " / " + total + " functions");
            }
        } finally {
            decompiler.dispose();
        }
        println("MyCompiler: wrote " + done + " functions (" + failed + " failed) to " + output.getAbsolutePath());
    }
}
