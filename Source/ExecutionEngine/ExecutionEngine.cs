﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VC;
using BoogiePL = Microsoft.Boogie;


namespace Microsoft.Boogie
{

  #region Output printing

  public interface OutputPrinter
  {
    void ErrorWriteLine(string s);
    void ErrorWriteLine(string format, params object[] args);
    void AdvisoryWriteLine(string format, params object[] args);
    void Inform(string s);
    void WriteTrailer(int verified, int errors, int inconclusives, int timeOuts, int outOfMemories);
    void ReportBplError(IToken tok, string message, bool error, bool showBplLocation);
  }


  public class ConsolePrinter : OutputPrinter
  {
    public void ErrorWriteLine(string s)
    {
      Contract.Requires(s != null);
      if (!s.Contains("Error: ") && !s.Contains("Error BP"))
      {
        Console.WriteLine(s);
        return;
      }

      // split the string up into its first line and the remaining lines
      string remaining = null;
      int i = s.IndexOf('\r');
      if (0 <= i)
      {
        remaining = s.Substring(i + 1);
        if (remaining.StartsWith("\n"))
        {
          remaining = remaining.Substring(1);
        }
        s = s.Substring(0, i);
      }

      ConsoleColor col = Console.ForegroundColor;
      Console.ForegroundColor = ConsoleColor.Red;
      Console.WriteLine(s);
      Console.ForegroundColor = col;

      if (remaining != null)
      {
        Console.WriteLine(remaining);
      }
    }


    public void ErrorWriteLine(string format, params object[] args)
    {
      Contract.Requires(format != null);
      string s = string.Format(format, args);
      ErrorWriteLine(s);
    }


    public void AdvisoryWriteLine(string format, params object[] args)
    {
      Contract.Requires(format != null);
      ConsoleColor col = Console.ForegroundColor;
      Console.ForegroundColor = ConsoleColor.Yellow;
      Console.WriteLine(format, args);
      Console.ForegroundColor = col;
    }


    /// <summary>
    /// Inform the user about something and proceed with translation normally.
    /// Print newline after the message.
    /// </summary>
    public void Inform(string s)
    {
      if (CommandLineOptions.Clo.Trace || CommandLineOptions.Clo.TraceProofObligations)
      {
        Console.WriteLine(s);
      }
    }


    public void WriteTrailer(int verified, int errors, int inconclusives, int timeOuts, int outOfMemories)
    {
      Contract.Requires(0 <= errors && 0 <= inconclusives && 0 <= timeOuts && 0 <= outOfMemories);
      Console.WriteLine();
      if (CommandLineOptions.Clo.vcVariety == CommandLineOptions.VCVariety.Doomed)
      {
        Console.Write("{0} finished with {1} credible, {2} doomed{3}", CommandLineOptions.Clo.DescriptiveToolName, verified, errors, errors == 1 ? "" : "s");
      }
      else
      {
        Console.Write("{0} finished with {1} verified, {2} error{3}", CommandLineOptions.Clo.DescriptiveToolName, verified, errors, errors == 1 ? "" : "s");
      }
      if (inconclusives != 0)
      {
        Console.Write(", {0} inconclusive{1}", inconclusives, inconclusives == 1 ? "" : "s");
      }
      if (timeOuts != 0)
      {
        Console.Write(", {0} time out{1}", timeOuts, timeOuts == 1 ? "" : "s");
      }
      if (outOfMemories != 0)
      {
        Console.Write(", {0} out of memory", outOfMemories);
      }
      Console.WriteLine();
      Console.Out.Flush();
    }


    public virtual void ReportBplError(IToken tok, string message, bool error, bool showBplLocation)
    {
      Contract.Requires(message != null);
      string s;
      if (tok != null && showBplLocation)
      {
        s = string.Format("{0}({1},{2}): {3}", tok.filename, tok.line, tok.col, message);
      }
      else
      {
        s = message;
      }
      if (error)
      {
        ErrorWriteLine(s);
      }
      else
      {
        Console.WriteLine(s);
      }
    }
  }

  #endregion


  public enum PipelineOutcome
  {
    Done,
    ResolutionError,
    TypeCheckingError,
    ResolvedAndTypeChecked,
    FatalError,
    VerificationCompleted
  }


  #region Error reporting

  public delegate void ErrorReporterDelegate(ErrorInformation errInfo);


  public class ErrorInformationFactory
  {
    public virtual ErrorInformation CreateErrorInformation(IToken tok, string msg, string requestId = null)
    {
      Contract.Requires(tok != null);
      Contract.Requires(1 <= tok.line && 1 <= tok.col);
      Contract.Requires(msg != null);

      return ErrorInformation.CreateErrorInformation(tok, msg, requestId);
    }
  }


  public class ErrorInformation
  {
    public IToken Tok;
    public string Msg;
    public readonly List<AuxErrorInfo> Aux = new List<AuxErrorInfo>();
    public string RequestId { get; set; }

    public struct AuxErrorInfo
    {
      public readonly IToken Tok;
      public readonly string Msg;

      public AuxErrorInfo(IToken tok, string msg)
      {
        Tok = tok;
        Msg = CleanUp(msg);
      }
    }

    protected ErrorInformation(IToken tok, string msg)
    {
      Contract.Requires(tok != null);
      Contract.Requires(1 <= tok.line && 1 <= tok.col);
      Contract.Requires(msg != null);

      Tok = tok;
      Msg = CleanUp(msg);
    }

    internal static ErrorInformation CreateErrorInformation(IToken tok, string msg, string requestId = null)
    {
      var result = new ErrorInformation(tok, msg);
      result.RequestId = requestId;
      return result;
    }

    public virtual void AddAuxInfo(IToken tok, string msg)
    {
      Contract.Requires(tok != null);
      Contract.Requires(1 <= tok.line && 1 <= tok.col);
      Contract.Requires(msg != null);
      Aux.Add(new AuxErrorInfo(tok, msg));
    }

    protected static string CleanUp(string msg)
    {
      if (msg.ToLower().StartsWith("error: "))
      {
        return msg.Substring(7);
      }
      else
      {
        return msg;
      }
    }
  }

  #endregion


  public class ExecutionEngine
  {
    public static OutputPrinter printer;

    public static ErrorInformationFactory errorInformationFactory = new ErrorInformationFactory();

    public readonly static VerificationResultCache Cache = new VerificationResultCache();

    static LinearTypechecker linearTypechecker;


    public static void ProcessFiles(List<string> fileNames, bool lookForSnapshots = true)
    {
      Contract.Requires(cce.NonNullElements(fileNames));

      if (CommandLineOptions.Clo.VerifySnapshots && lookForSnapshots)
      {
        var snapshotsByVersion = new List<List<string>>();
        for (int version = 0; true; version++)
        {
          var nextSnapshot = new List<string>();
          foreach (var name in fileNames)
          {
            var versionedName = name.Replace(Path.GetExtension(name), ".v" + version + Path.GetExtension(name));
            if (File.Exists(versionedName))
            {
              nextSnapshot.Add(versionedName);
            }
          }
          if (nextSnapshot.Any())
          {
            snapshotsByVersion.Add(nextSnapshot);
          }
          else
          {
            break;
          }
        }

        foreach (var s in snapshotsByVersion)
        {
          ProcessFiles(new List<string>(s), false);
        }
        return;
      }

      using (XmlFileScope xf = new XmlFileScope(CommandLineOptions.Clo.XmlSink, fileNames[fileNames.Count - 1]))
      {
        //BoogiePL.Errors.count = 0;
        Program program = ParseBoogieProgram(fileNames, false);
        if (program == null)
          return;
        if (CommandLineOptions.Clo.PrintFile != null)
        {
          PrintBplFile(CommandLineOptions.Clo.PrintFile, program, false);
        }

        PipelineOutcome oc = ResolveAndTypecheck(program, fileNames[fileNames.Count - 1]);
        if (oc != PipelineOutcome.ResolvedAndTypeChecked)
          return;
        //BoogiePL.Errors.count = 0;

        // Do bitvector analysis
        if (CommandLineOptions.Clo.DoBitVectorAnalysis)
        {
          Microsoft.Boogie.BitVectorAnalysis.DoBitVectorAnalysis(program);
          PrintBplFile(CommandLineOptions.Clo.BitVectorAnalysisOutputBplFile, program, false);
          return;
        }

        if (CommandLineOptions.Clo.PrintCFGPrefix != null)
        {
          foreach (var impl in program.TopLevelDeclarations.OfType<Implementation>())
          {
            using (StreamWriter sw = new StreamWriter(CommandLineOptions.Clo.PrintCFGPrefix + "." + impl.Name + ".dot"))
            {
              sw.Write(program.ProcessLoops(impl).ToDot());
            }
          }
        }

        if (CommandLineOptions.Clo.OwickiGriesDesugaredOutputFile != null)
        {
          OwickiGriesTransform ogTransform = new OwickiGriesTransform(linearTypechecker);
          ogTransform.Transform();
          var eraser = new LinearEraser();
          eraser.VisitProgram(program);
          int oldPrintUnstructured = CommandLineOptions.Clo.PrintUnstructured;
          CommandLineOptions.Clo.PrintUnstructured = 1;
          PrintBplFile(CommandLineOptions.Clo.OwickiGriesDesugaredOutputFile, program, false, false);
          CommandLineOptions.Clo.PrintUnstructured = oldPrintUnstructured;
        }

        EliminateDeadVariablesAndInline(program);

        if (CommandLineOptions.Clo.StagedHoudini > 0)
        {
          var candidateDependenceAnalyser = new CandidateDependenceAnalyser(program);
          candidateDependenceAnalyser.Analyse();
          candidateDependenceAnalyser.ApplyStages();
          if (CommandLineOptions.Clo.Trace)
          {
            candidateDependenceAnalyser.dump();
            int oldPrintUnstructured = CommandLineOptions.Clo.PrintUnstructured;
            CommandLineOptions.Clo.PrintUnstructured = 2;
            PrintBplFile("staged.bpl", program, false, false);
            CommandLineOptions.Clo.PrintUnstructured = oldPrintUnstructured;
          }
        }

        int errorCount, verified, inconclusives, timeOuts, outOfMemories;
        oc = InferAndVerify(program, out errorCount, out verified, out inconclusives, out timeOuts, out outOfMemories);
        switch (oc)
        {
          case PipelineOutcome.Done:
          case PipelineOutcome.VerificationCompleted:
            printer.WriteTrailer(verified, errorCount, inconclusives, timeOuts, outOfMemories);
            break;
          default:
            break;
        }
      }
    }


    public static void PrintBplFile(string filename, Program program, bool allowPrintDesugaring, bool setTokens = true)
    {
      Contract.Requires(program != null);
      Contract.Requires(filename != null);
      bool oldPrintDesugaring = CommandLineOptions.Clo.PrintDesugarings;
      if (!allowPrintDesugaring)
      {
        CommandLineOptions.Clo.PrintDesugarings = false;
      }
      using (TokenTextWriter writer = filename == "-" ?
                                      new TokenTextWriter("<console>", Console.Out, setTokens) :
                                      new TokenTextWriter(filename, setTokens))
      {
        if (CommandLineOptions.Clo.ShowEnv != CommandLineOptions.ShowEnvironment.Never)
        {
          writer.WriteLine("// " + CommandLineOptions.Clo.Version);
          writer.WriteLine("// " + CommandLineOptions.Clo.Environment);
        }
        writer.WriteLine();
        program.Emit(writer);
      }
      CommandLineOptions.Clo.PrintDesugarings = oldPrintDesugaring;
    }


    /// <summary>
    /// Parse the given files into one Boogie program.  If an I/O or parse error occurs, an error will be printed
    /// and null will be returned.  On success, a non-null program is returned.
    /// </summary>
    public static Program ParseBoogieProgram(List<string> fileNames, bool suppressTraceOutput)
    {
      Contract.Requires(cce.NonNullElements(fileNames));
      //BoogiePL.Errors.count = 0;
      Program program = null;
      bool okay = true;
      for (int fileId = 0; fileId < fileNames.Count; fileId++)
      {
        string bplFileName = fileNames[fileId];
        if (!suppressTraceOutput)
        {
          if (CommandLineOptions.Clo.XmlSink != null)
          {
            CommandLineOptions.Clo.XmlSink.WriteFileFragment(bplFileName);
          }
          if (CommandLineOptions.Clo.Trace)
          {
            Console.WriteLine("Parsing " + bplFileName);
          }
        }

        Program programSnippet;
        int errorCount;
        try
        {
          var defines = new List<string>() { "FILE_" + fileId };
          errorCount = BoogiePL.Parser.Parse(bplFileName, defines, out programSnippet);
          if (programSnippet == null || errorCount != 0)
          {
            Console.WriteLine("{0} parse errors detected in {1}", errorCount, bplFileName);
            okay = false;
            continue;
          }
        }
        catch (IOException e)
        {
          printer.ErrorWriteLine("Error opening file \"{0}\": {1}", bplFileName, e.Message);
          okay = false;
          continue;
        }
        if (program == null)
        {
          program = programSnippet;
        }
        else if (programSnippet != null)
        {
          program.TopLevelDeclarations.AddRange(programSnippet.TopLevelDeclarations);
        }
      }
      if (!okay)
      {
        return null;
      }
      else if (program == null)
      {
        return new Program();
      }
      else
      {
        return program;
      }
    }


    /// <summary>
    /// Resolves and type checks the given Boogie program.  Any errors are reported to the
    /// console.  Returns:
    ///  - Done if no errors occurred, and command line specified no resolution or no type checking.
    ///  - ResolutionError if a resolution error occurred
    ///  - TypeCheckingError if a type checking error occurred
    ///  - ResolvedAndTypeChecked if both resolution and type checking succeeded
    /// </summary>
    public static PipelineOutcome ResolveAndTypecheck(Program program, string bplFileName)
    {
      Contract.Requires(program != null);
      Contract.Requires(bplFileName != null);
      // ---------- Resolve ------------------------------------------------------------

      if (CommandLineOptions.Clo.NoResolve)
      {
        return PipelineOutcome.Done;
      }

      int errorCount = program.Resolve();
      if (errorCount != 0)
      {
        Console.WriteLine("{0} name resolution errors detected in {1}", errorCount, bplFileName);
        return PipelineOutcome.ResolutionError;
      }

      // ---------- Type check ------------------------------------------------------------

      if (CommandLineOptions.Clo.NoTypecheck)
      {
        return PipelineOutcome.Done;
      }

      errorCount = program.Typecheck();
      if (errorCount != 0)
      {
        Console.WriteLine("{0} type checking errors detected in {1}", errorCount, bplFileName);
        return PipelineOutcome.TypeCheckingError;
      }

      linearTypechecker = new LinearTypechecker(program);
      linearTypechecker.Typecheck();
      if (linearTypechecker.errorCount == 0)
      {
        linearTypechecker.Transform();
      }
      else
      {
        Console.WriteLine("{0} type checking errors detected in {1}", linearTypechecker.errorCount, bplFileName);
        return PipelineOutcome.TypeCheckingError;
      }

      if (CommandLineOptions.Clo.PrintFile != null && CommandLineOptions.Clo.PrintDesugarings)
      {
        // if PrintDesugaring option is engaged, print the file here, after resolution and type checking
        PrintBplFile(CommandLineOptions.Clo.PrintFile, program, true);
      }

      return PipelineOutcome.ResolvedAndTypeChecked;
    }


    public static void EliminateDeadVariablesAndInline(Program program)
    {
      Contract.Requires(program != null);
      // Eliminate dead variables
      Microsoft.Boogie.UnusedVarEliminator.Eliminate(program);

      // Collect mod sets
      if (CommandLineOptions.Clo.DoModSetAnalysis)
      {
        Microsoft.Boogie.ModSetCollector.DoModSetAnalysis(program);
      }

      // Coalesce blocks
      if (CommandLineOptions.Clo.CoalesceBlocks)
      {
        if (CommandLineOptions.Clo.Trace)
          Console.WriteLine("Coalescing blocks...");
        Microsoft.Boogie.BlockCoalescer.CoalesceBlocks(program);
      }

      // Inline
      var TopLevelDeclarations = cce.NonNull(program.TopLevelDeclarations);

      if (CommandLineOptions.Clo.ProcedureInlining != CommandLineOptions.Inlining.None)
      {
        bool inline = false;
        foreach (var d in TopLevelDeclarations)
        {
          if (d.FindExprAttribute("inline") != null)
          {
            inline = true;
          }
        }
        if (inline)
        {
          foreach (var d in TopLevelDeclarations)
          {
            var impl = d as Implementation;
            if (impl != null)
            {
              impl.OriginalBlocks = impl.Blocks;
              impl.OriginalLocVars = impl.LocVars;
            }
          }
          foreach (var d in TopLevelDeclarations)
          {
            var impl = d as Implementation;
            if (impl != null && !impl.SkipVerification)
            {
              Inliner.ProcessImplementation(program, impl);
            }
          }
          foreach (var d in TopLevelDeclarations)
          {
            var impl = d as Implementation;
            if (impl != null)
            {
              impl.OriginalBlocks = null;
              impl.OriginalLocVars = null;
            }
          }
        }
      }
    }


    /// <summary>
    /// Given a resolved and type checked Boogie program, infers invariants for the program
    /// and then attempts to verify it.  Returns:
    ///  - Done if command line specified no verification
    ///  - FatalError if a fatal error occurred, in which case an error has been printed to console
    ///  - VerificationCompleted if inference and verification completed, in which the out
    ///    parameters contain meaningful values
    /// </summary>
    public static PipelineOutcome InferAndVerify(Program program,
                                                 out int errorCount, out int verified, out int inconclusives, out int timeOuts, out int outOfMemories,
                                                 ErrorReporterDelegate er = null, string requestId = null)
    {
      Contract.Requires(program != null);
      Contract.Ensures(0 <= Contract.ValueAtReturn(out inconclusives) && 0 <= Contract.ValueAtReturn(out timeOuts));

      errorCount = verified = inconclusives = timeOuts = outOfMemories = 0;

      #region Infer invariants using Abstract Interpretation

      // Always use (at least) intervals, if not specified otherwise (e.g. with the "/noinfer" switch)
      if (CommandLineOptions.Clo.UseAbstractInterpretation)
      {
        if (!CommandLineOptions.Clo.Ai.J_Intervals && !CommandLineOptions.Clo.Ai.J_Trivial)
        {
          // use /infer:j as the default
          CommandLineOptions.Clo.Ai.J_Intervals = true;
        }
      }
      Microsoft.Boogie.AbstractInterpretation.NativeAbstractInterpretation.RunAbstractInterpretation(program);

      #endregion

      #region Do some preprocessing on the program (e.g., loop unrolling, lambda expansion)

      if (CommandLineOptions.Clo.LoopUnrollCount != -1)
      {
        program.UnrollLoops(CommandLineOptions.Clo.LoopUnrollCount, CommandLineOptions.Clo.SoundLoopUnrolling);
      }

      Dictionary<string, Dictionary<string, Block>> extractLoopMappingInfo = null;
      if (CommandLineOptions.Clo.ExtractLoops)
      {
        extractLoopMappingInfo = program.ExtractLoops();
      }

      if (CommandLineOptions.Clo.PrintInstrumented)
      {
        program.Emit(new TokenTextWriter(Console.Out));
      }

      if (CommandLineOptions.Clo.ExpandLambdas)
      {
        LambdaHelper.ExpandLambdas(program);
        //PrintBplFile ("-", program, true);
      }

      #endregion

      if (!CommandLineOptions.Clo.Verify)
      {
        return PipelineOutcome.Done;
      }

      #region Run Houdini and verify
      if (CommandLineOptions.Clo.ContractInfer)
      {
        return RunHoudini(program, ref errorCount, ref verified, ref inconclusives, ref timeOuts, ref outOfMemories, er);
      }
      #endregion

      #region Set up the VC generation

      ConditionGeneration vcgen = null;
      try
      {
        if (CommandLineOptions.Clo.vcVariety == CommandLineOptions.VCVariety.Doomed)
        {
          vcgen = new DCGen(program, CommandLineOptions.Clo.SimplifyLogFilePath, CommandLineOptions.Clo.SimplifyLogFileAppend);
        }
        else if (CommandLineOptions.Clo.FixedPointEngine != null)
        {
          vcgen = new FixedpointVC(program, CommandLineOptions.Clo.SimplifyLogFilePath, CommandLineOptions.Clo.SimplifyLogFileAppend);
        }
        else if (CommandLineOptions.Clo.StratifiedInlining > 0)
        {
          vcgen = new StratifiedVCGen(program, CommandLineOptions.Clo.SimplifyLogFilePath, CommandLineOptions.Clo.SimplifyLogFileAppend);
        }
        else
        {
          vcgen = new VCGen(program, CommandLineOptions.Clo.SimplifyLogFilePath, CommandLineOptions.Clo.SimplifyLogFileAppend);
        }
      }
      catch (ProverException e)
      {
        printer.ErrorWriteLine("Fatal Error: ProverException: {0}", e);
        return PipelineOutcome.FatalError;
      }

      #endregion

      #region Select and prioritize implementations that should be verified

      var prioritizedImpls =
        from impl in program.TopLevelDeclarations.OfType<Implementation>()
        where impl != null && CommandLineOptions.Clo.UserWantsToCheckRoutine(cce.NonNull(impl.Name)) && !impl.SkipVerification
        orderby impl.Priority descending
        select impl;

      // operate on a stable copy, in case it gets updated while we're running
      var stablePrioritizedImpls = prioritizedImpls.ToArray();

      #endregion

      #region Verify each implementation

      foreach (var impl in stablePrioritizedImpls)
      {
        List<Counterexample/*!*/>/*?*/ errors;
        VCGen.Outcome outcome;

        #region Initialize counters and print trace output

        int prevAssertionCount = vcgen.CumulativeAssertionCount;
        DateTime start = DateTime.UtcNow;

        printer.Inform(string.Format("\nVerifying {0} ...", impl.Name));

        if (CommandLineOptions.Clo.XmlSink != null)
        {
          CommandLineOptions.Clo.XmlSink.WriteStartMethod(impl.Name, start);
        }

        #endregion

        #region Record the checksum of the dependencies of this implementation

        string depsChecksum = null;
        if (CommandLineOptions.Clo.VerifySnapshots)
        {
          depsChecksum = DependencyCollector.DependenciesChecksum(impl);
        }

        #endregion

        #region Verify the implementation

        try
        {
          if (CommandLineOptions.Clo.inferLeastForUnsat != null)
          {
            var svcgen = vcgen as VC.StratifiedVCGen;
            Contract.Assert(svcgen != null);
            var ss = new HashSet<string>();
            foreach (var tdecl in program.TopLevelDeclarations)
            {
              var c = tdecl as Constant;
              if (c == null || !c.Name.StartsWith(CommandLineOptions.Clo.inferLeastForUnsat)) continue;
              ss.Add(c.Name);
            }
            outcome = svcgen.FindLeastToVerify(impl, ref ss);
            errors = new List<Counterexample>();
            Console.WriteLine("Result: {0}", string.Join(" ", ss));
          }
          else
          {
            if (!CommandLineOptions.Clo.VerifySnapshots || Cache.NeedsToBeVerified(impl, depsChecksum))
            {
              outcome = vcgen.VerifyImplementation(impl, out errors, requestId);

              if (CommandLineOptions.Clo.ExtractLoops && errors != null)
              {
                var vcg = vcgen as VCGen;
                if (vcg != null)
                {
                  for (int i = 0; i < errors.Count; i++)
                  {
                    errors[i] = vcg.extractLoopTrace(errors[i], impl.Name, program, extractLoopMappingInfo);
                  }
                }
              }
            }
            else
            {
              printer.Inform(string.Format("Retrieving cached verification result for implementation {0}...", impl.Name));

              var result = Cache.Lookup(impl.Id);
              Contract.Assert(result != null);
              outcome = result.Outcome;
              errors = result.Errors;
            }
          }
        }
        catch (VCGenException e)
        {
          printer.ReportBplError(impl.tok, String.Format("Error BP5010: {0}  Encountered in implementation {1}.", e.Message, impl.Name), true, true);
          errors = null;
          outcome = VCGen.Outcome.Inconclusive;
        }
        catch (UnexpectedProverOutputException upo)
        {
          printer.AdvisoryWriteLine("Advisory: {0} SKIPPED because of internal error: unexpected prover output: {1}", impl.Name, upo.Message);
          errors = null;
          outcome = VCGen.Outcome.Inconclusive;
        }

        #endregion

        #region Cache the verification result

        if (CommandLineOptions.Clo.VerifySnapshots && !string.IsNullOrEmpty(impl.Checksum))
        {
          Cache.Insert(impl.Id, new VerificationResult(requestId, impl.Checksum, depsChecksum, outcome, errors));
        }

        #endregion

        #region Process the verification results and statistics

        string timeIndication;
        DateTime end;
        TimeSpan elapsed;
        CollectTimeStatistics(vcgen.CumulativeAssertionCount - prevAssertionCount, start, out timeIndication, out end, out elapsed);

        ProcessOutcome(outcome, errors, timeIndication, ref errorCount, ref verified, ref inconclusives, ref timeOuts, ref outOfMemories, er, impl, requestId);

        if (CommandLineOptions.Clo.XmlSink != null)
        {
          CommandLineOptions.Clo.XmlSink.WriteEndMethod(outcome.ToString().ToLowerInvariant(), end, elapsed);
        }
        if (outcome == VCGen.Outcome.Errors || CommandLineOptions.Clo.Trace)
        {
          Console.Out.Flush();
        }

        #endregion
      }

      vcgen.Close();
      cce.NonNull(CommandLineOptions.Clo.TheProverFactory).Close();

      #endregion

      return PipelineOutcome.VerificationCompleted;
    }


    private static PipelineOutcome RunHoudini(Program program, ref int errorCount, ref int verified, ref int inconclusives, ref int timeOuts, ref int outOfMemories, ErrorReporterDelegate er)
    {
      if (CommandLineOptions.Clo.AbstractHoudini != null)
      {
        return RunAbstractHoudini(program, ref errorCount, ref verified, ref inconclusives, ref timeOuts, ref outOfMemories, er);
      }

      Houdini.Houdini houdini = new Houdini.Houdini(program);
      Houdini.HoudiniOutcome outcome = houdini.PerformHoudiniInference();

      if (CommandLineOptions.Clo.PrintAssignment)
      {
        Console.WriteLine("Assignment computed by Houdini:");
        foreach (var x in outcome.assignment)
        {
          Console.WriteLine(x.Key + " = " + x.Value);
        }
      }

      if (CommandLineOptions.Clo.Trace)
      {
        int numTrueAssigns = 0;
        foreach (var x in outcome.assignment)
        {
          if (x.Value)
            numTrueAssigns++;
        }
        Console.WriteLine("Number of true assignments = " + numTrueAssigns);
        Console.WriteLine("Number of false assignments = " + (outcome.assignment.Count - numTrueAssigns));
        Console.WriteLine("Prover time = " + Houdini.HoudiniSession.proverTime.ToString("F2"));
        Console.WriteLine("Unsat core prover time = " + Houdini.HoudiniSession.unsatCoreProverTime.ToString("F2"));
        Console.WriteLine("Number of prover queries = " + Houdini.HoudiniSession.numProverQueries);
        Console.WriteLine("Number of unsat core prover queries = " + Houdini.HoudiniSession.numUnsatCoreProverQueries);
        Console.WriteLine("Number of unsat core prunings = " + Houdini.HoudiniSession.numUnsatCorePrunings);
      }

      foreach (Houdini.VCGenOutcome x in outcome.implementationOutcomes.Values)
      {
        ProcessOutcome(x.outcome, x.errors, "", ref errorCount, ref verified, ref inconclusives, ref timeOuts, ref outOfMemories, er);
      }
      //errorCount = outcome.ErrorCount;
      //verified = outcome.Verified;
      //inconclusives = outcome.Inconclusives;
      //timeOuts = outcome.TimeOuts;
      //outOfMemories = 0;
      return PipelineOutcome.Done;
    }


    private static PipelineOutcome RunAbstractHoudini(Program program, ref int errorCount, ref int verified, ref int inconclusives, ref int timeOuts, ref int outOfMemories, ErrorReporterDelegate er)
    {
      //CommandLineOptions.Clo.PrintErrorModel = 1;
      CommandLineOptions.Clo.UseProverEvaluate = true;
      CommandLineOptions.Clo.ModelViewFile = "z3model";
      CommandLineOptions.Clo.UseArrayTheory = true;
      CommandLineOptions.Clo.TypeEncodingMethod = CommandLineOptions.TypeEncoding.Monomorphic;
      Houdini.AbstractDomainFactory.Initialize();
      var domain = Houdini.AbstractDomainFactory.GetInstance(CommandLineOptions.Clo.AbstractHoudini);

      // Run Abstract Houdini
      var abs = new Houdini.AbsHoudini(program, domain);
      var absout = abs.ComputeSummaries();
      ProcessOutcome(absout.outcome, absout.errors, "", ref errorCount, ref verified, ref inconclusives, ref timeOuts, ref outOfMemories, er);

      //Houdini.PredicateAbs.Initialize(program);
      //var abs = new Houdini.AbstractHoudini(program);
      //abs.computeSummaries(new Houdini.PredicateAbs(program.TopLevelDeclarations.OfType<Implementation>().First().Name));

      return PipelineOutcome.Done;
    }


    private static void CollectTimeStatistics(int proofObligationCount, DateTime start, out string timeIndication, out DateTime end, out TimeSpan elapsed)
    {
      timeIndication = "";
      end = DateTime.UtcNow;
      elapsed = end - start;
      if (CommandLineOptions.Clo.Trace)
      {
        timeIndication = string.Format("  [{0:F3} s, {1} proof obligation{2}]  ", elapsed.TotalSeconds, proofObligationCount, proofObligationCount == 1 ? "" : "s");
      }
      else if (CommandLineOptions.Clo.TraceProofObligations)
      {
        timeIndication = string.Format("  [{0} proof obligation{1}]  ", proofObligationCount, proofObligationCount == 1 ? "" : "s");
      }
    }


    private static void ProcessOutcome(VC.VCGen.Outcome outcome, List<Counterexample> errors, string timeIndication,
                                       ref int errorCount, ref int verified, ref int inconclusives, ref int timeOuts, ref int outOfMemories, ErrorReporterDelegate er = null, Implementation impl = null, string requestId = null)
    {
      switch (outcome)
      {
        default:
          Contract.Assert(false);  // unexpected outcome
          throw new cce.UnreachableException();
        case VCGen.Outcome.ReachedBound:
          printer.Inform(String.Format("{0}verified", timeIndication));
          Console.WriteLine(string.Format("Stratified Inlining: Reached recursion bound of {0}", CommandLineOptions.Clo.RecursionBound));
          verified++;
          break;
        case VCGen.Outcome.Correct:
          if (CommandLineOptions.Clo.vcVariety == CommandLineOptions.VCVariety.Doomed)
          {
            printer.Inform(String.Format("{0}credible", timeIndication));
            verified++;
          }
          else
          {
            printer.Inform(String.Format("{0}verified", timeIndication));
            verified++;
          }
          break;
        case VCGen.Outcome.TimedOut:
          timeOuts++;
          printer.Inform(String.Format("{0}timed out", timeIndication));
          if (er != null && impl != null)
          {
            er(errorInformationFactory.CreateErrorInformation(impl.tok, string.Format("Verification timed out after {0} seconds ({1})", CommandLineOptions.Clo.ProverKillTime, impl.Name), requestId));
          }
          break;
        case VCGen.Outcome.OutOfMemory:
          outOfMemories++;
          printer.Inform(String.Format("{0}out of memory", timeIndication));
          if (er != null && impl != null)
          {
            er(errorInformationFactory.CreateErrorInformation(impl.tok, "Verification out of memory (" + impl.Name + ")", requestId));
          }
          break;
        case VCGen.Outcome.Inconclusive:
          inconclusives++;
          printer.Inform(String.Format("{0}inconclusive", timeIndication));
          if (er != null && impl != null)
          {
            er(errorInformationFactory.CreateErrorInformation(impl.tok, "Verification inconclusive (" + impl.Name + ")", requestId));
          }
          break;
        case VCGen.Outcome.Errors:
          if (CommandLineOptions.Clo.vcVariety == CommandLineOptions.VCVariety.Doomed)
          {
            printer.Inform(String.Format("{0}doomed", timeIndication));
            errorCount++;
          }
          Contract.Assert(errors != null);  // guaranteed by postcondition of VerifyImplementation
          break;
      }
      if (errors != null)
      {
        // BP1xxx: Parsing errors
        // BP2xxx: Name resolution errors
        // BP3xxx: Typechecking errors
        // BP4xxx: Abstract interpretation errors (Is there such a thing?)
        // BP5xxx: Verification errors

        var cause = "Error";
        if (outcome == VCGen.Outcome.TimedOut)
        {
          cause = "Timed out on";
        }
        else if (outcome == VCGen.Outcome.OutOfMemory)
        {
          cause = "Out of memory on";
        }
        // TODO(wuestholz): Take the error cause into account when writing to the XML sink.

        errors.Sort(new CounterexampleComparer());
        foreach (Counterexample error in errors)
        {
          ErrorInformation errorInfo;

          if (error is CallCounterexample)
          {
            CallCounterexample err = (CallCounterexample)error;
            if (!CommandLineOptions.Clo.ForceBplErrors && err.FailingRequires.ErrorMessage != null)
            {
              printer.ReportBplError(err.FailingRequires.tok, err.FailingRequires.ErrorMessage, true, false);
            }
            else
            {
              printer.ReportBplError(err.FailingCall.tok, cause + " BP5002: A precondition for this call might not hold.", true, true);
              printer.ReportBplError(err.FailingRequires.tok, "Related location: This is the precondition that might not hold.", false, true);
            }

            errorInfo = errorInformationFactory.CreateErrorInformation(err.FailingCall.tok, cause + ": " + (err.FailingCall.ErrorData as string ?? "A precondition for this call might not hold."), err.RequestId);
            errorInfo.AddAuxInfo(err.FailingRequires.tok, err.FailingRequires.ErrorData as string ?? "Related location: This is the precondition that might not hold.");

            if (CommandLineOptions.Clo.XmlSink != null)
            {
              CommandLineOptions.Clo.XmlSink.WriteError("precondition violation", err.FailingCall.tok, err.FailingRequires.tok, error.Trace);
            }
          }
          else if (error is ReturnCounterexample)
          {
            ReturnCounterexample err = (ReturnCounterexample)error;
            if (!CommandLineOptions.Clo.ForceBplErrors && err.FailingEnsures.ErrorMessage != null)
            {
              printer.ReportBplError(err.FailingEnsures.tok, err.FailingEnsures.ErrorMessage, true, false);
            }
            else
            {
              printer.ReportBplError(err.FailingReturn.tok, cause + " BP5003: A postcondition might not hold on this return path.", true, true);
              printer.ReportBplError(err.FailingEnsures.tok, "Related location: This is the postcondition that might not hold.", false, true);
            }

            errorInfo = errorInformationFactory.CreateErrorInformation(err.FailingReturn.tok, cause + ": " + "A postcondition might not hold on this return path.", err.RequestId);
            errorInfo.AddAuxInfo(err.FailingEnsures.tok, err.FailingEnsures.ErrorData as string ?? "Related location: This is the postcondition that might not hold.");

            if (CommandLineOptions.Clo.XmlSink != null)
            {
              CommandLineOptions.Clo.XmlSink.WriteError("postcondition violation", err.FailingReturn.tok, err.FailingEnsures.tok, error.Trace);
            }
          }
          else // error is AssertCounterexample
          {
            AssertCounterexample err = (AssertCounterexample)error;
            if (err.FailingAssert is LoopInitAssertCmd)
            {
              printer.ReportBplError(err.FailingAssert.tok, cause + " BP5004: This loop invariant might not hold on entry.", true, true);

              errorInfo = errorInformationFactory.CreateErrorInformation(err.FailingAssert.tok, cause + ": " + "This loop invariant might not hold on entry.", err.RequestId);

              if (CommandLineOptions.Clo.XmlSink != null)
              {
                CommandLineOptions.Clo.XmlSink.WriteError("loop invariant entry violation", err.FailingAssert.tok, null, error.Trace);
              }
            }
            else if (err.FailingAssert is LoopInvMaintainedAssertCmd)
            {
              // this assertion is a loop invariant which is not maintained
              printer.ReportBplError(err.FailingAssert.tok, cause + " BP5005: This loop invariant might not be maintained by the loop.", true, true);

              errorInfo = errorInformationFactory.CreateErrorInformation(err.FailingAssert.tok, cause + ": " + "This loop invariant might not be maintained by the loop.", err.RequestId);

              if (CommandLineOptions.Clo.XmlSink != null)
              {
                CommandLineOptions.Clo.XmlSink.WriteError("loop invariant maintenance violation", err.FailingAssert.tok, null, error.Trace);
              }
            }
            else
            {
              if (!CommandLineOptions.Clo.ForceBplErrors && err.FailingAssert.ErrorMessage != null)
              {
                printer.ReportBplError(err.FailingAssert.tok, err.FailingAssert.ErrorMessage, true, false);
              }
              else if (err.FailingAssert.ErrorData is string)
              {
                printer.ReportBplError(err.FailingAssert.tok, (string)err.FailingAssert.ErrorData, true, true);
              }
              else
              {
                printer.ReportBplError(err.FailingAssert.tok, cause + " BP5001: This assertion might not hold.", true, true);
              }

              var msg = err.FailingAssert.ErrorData as string;
              if (msg == null)
              {
                msg = "This assertion might not hold.";
              }
              errorInfo = errorInformationFactory.CreateErrorInformation(err.FailingAssert.tok, cause + ": " + msg, err.RequestId);

              if (CommandLineOptions.Clo.XmlSink != null)
              {
                CommandLineOptions.Clo.XmlSink.WriteError("assertion violation", err.FailingAssert.tok, null, error.Trace);
              }
            }
          }
          if (CommandLineOptions.Clo.EnhancedErrorMessages == 1)
          {
            foreach (string info in error.relatedInformation)
            {
              Contract.Assert(info != null);
              Console.WriteLine("       " + info);
            }
          }
          if (CommandLineOptions.Clo.ErrorTrace > 0)
          {
            Console.WriteLine("Execution trace:");
            error.Print(4);

            foreach (Block b in error.Trace)
            {
              if (!(CommandLineOptions.Clo.ErrorTrace == 1 && b.tok.line == -17 && b.tok.col == -4))
              {
                errorInfo.AddAuxInfo(b.tok, "Execution trace: " + b.Label);
              }
            }
          }
          if (CommandLineOptions.Clo.ModelViewFile != null)
          {
            error.PrintModel();
          }
          if (cause == "Error")
          {
            errorCount++;
          }
          if (er != null)
          {
            er(errorInfo);
          }
        }
        if (cause == "Error")
        {
          printer.Inform(String.Format("{0}error{1}", timeIndication, errors.Count == 1 ? "" : "s"));
        }
      }
    }

  }

}
