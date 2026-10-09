from pathlib import Path
import subprocess,os,json
root=Path(__file__).resolve().parent.parent
mutations=[
 ('panel visibility','src/MandoCode/Services/Rendering/AgentPresentationState.cs','(_open & (ModalPanels | AgentPanel.DirectoryBrowser)) != 0','false','FullyQualifiedName~PresentationBoundaryTests.ClosingNestedPanel'),
 ('callback ownership','src/MandoCode/Services/Rendering/CallbackRegistrations.cs','if (Equals(read(), callback)) write(null);','write(null);','FullyQualifiedName~PresentationBoundaryTests.OldComponentDisposal'),
 ('argument casing','src/MandoCode/Services/Commands/CliCommand.cs','raw[(space + 1)..].Trim()','raw[(space + 1)..].Trim().ToLowerInvariant()','FullyQualifiedName~PresentationBoundaryTests.CommandParsing'),
 ('approval cancellation','src/MandoCode/Services/Approval/ApprovalPromptGate.cs','_gate.WaitAsync(cancellationToken)','_gate.WaitAsync(CancellationToken.None)','FullyQualifiedName~ApprovalPromptGateTests.QueuedAcquire_IsCancellable')]
results=[]
for name,file,old,new,filter in mutations:
 p=root/file;original=p.read_bytes();source=original.decode('utf-8');assert source.count(old)==1,(name,source.count(old))
 try:
  p.write_bytes(source.replace(old,new).encode('utf-8'))
  result=subprocess.run(['dotnet','test','tests/MandoCode.Tests/MandoCode.Tests.csproj','-c','Release','-f','net10.0','--no-restore','--filter',filter,'--verbosity','quiet'],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=60,cwd=root)
  output=result.stdout+result.stderr
  detected=result.returncode!=0 and 'Failed:     1' in output and '[FAIL]' in output
  results.append({'mutation':name,'detected':detected,'exitCode':result.returncode})
  print(name, 'DETECTED' if detected else 'CHECK FAILED',flush=True)
  if not detected:print(output[-2200:],flush=True)
 finally:p.write_bytes(original)
report=root/'artifacts/mutation-results.json'
report.parent.mkdir(parents=True,exist_ok=True)
report.write_text(json.dumps(results,indent=2))
if not all(result['detected'] for result in results): raise SystemExit(1)
