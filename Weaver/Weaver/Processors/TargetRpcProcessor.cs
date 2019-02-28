// all the [TargetRpc] code from NetworkBehaviourProcessor in one place
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Mirror.Weaver
{
    public static class TargetRpcProcessor
    {
        const string k_TargetRpcPrefix = "InvokeTargetRpc";

        public static MethodDefinition ProcessTargetRpcInvoke(TypeDefinition td, MethodDefinition md)
        {
            MethodDefinition rpc = new MethodDefinition(RpcProcessor.k_RpcPrefix + md.Name, MethodAttributes.Family |
                    MethodAttributes.Static |
                    MethodAttributes.HideBySig,
                    Weaver.voidType);

            ILProcessor rpcWorker = rpc.Body.GetILProcessor();
            Instruction label = rpcWorker.Create(OpCodes.Nop);

            NetworkBehaviourProcessor.WriteClientActiveCheck(rpcWorker, md.Name, label, "TargetRPC");

            // setup for reader
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldarg_0));
            rpcWorker.Append(rpcWorker.Create(OpCodes.Castclass, td));

            if (!NetworkBehaviourProcessor.ProcessNetworkReaderParameters(td, md, rpcWorker, false))
                return null;

            // invoke actual command function
            rpcWorker.Append(rpcWorker.Create(OpCodes.Callvirt, md));
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ret));

            NetworkBehaviourProcessor.AddInvokeParameters(rpc.Parameters);

            return rpc;
        }

        /* generates code like:
        public void CallTargetTest (int param)
        {
            NetworkWriter writer = new NetworkWriter ();
            writer.WritePackedUInt32 ((uint)param);
            base.SendTargetRPCInternal (typeof(class), "TargetTest", val);
        }
        */
        public static MethodDefinition ProcessTargetRpcCall(TypeDefinition td, MethodDefinition md, CustomAttribute ca)
        {
            MethodDefinition rpc = new MethodDefinition("Call" +  md.Name, MethodAttributes.Public |
                    MethodAttributes.HideBySig,
                    Weaver.voidType);

            // add parameters
            foreach (ParameterDefinition pd in md.Parameters)
            {
                rpc.Parameters.Add(new ParameterDefinition(pd.Name, ParameterAttributes.None, pd.ParameterType));
            }

            ILProcessor rpcWorker = rpc.Body.GetILProcessor();

            NetworkBehaviourProcessor.WriteSetupLocals(rpcWorker);

            NetworkBehaviourProcessor.WriteCreateWriter(rpcWorker);

            // write all the arguments that the user passed to the TargetRpc call
            if (!NetworkBehaviourProcessor.WriteArguments(rpcWorker, md, "TargetRPC", false))
                return null;

            var rpcName = md.Name;
            int index = rpcName.IndexOf(k_TargetRpcPrefix);
            if (index > -1)
            {
                rpcName = rpcName.Substring(k_TargetRpcPrefix.Length);
            }

            // invoke SendInternal and return
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldarg_0)); // this
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldtoken, td));
            rpcWorker.Append(rpcWorker.Create(OpCodes.Call, Weaver.getTypeFromHandleReference)); // invokerClass
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldstr, rpcName));
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldloc_0)); // writer
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldc_I4, NetworkBehaviourProcessor.GetChannelId(ca)));
            rpcWorker.Append(rpcWorker.Create(OpCodes.Callvirt, Weaver.sendTargetRpcInternal));

            rpcWorker.Append(rpcWorker.Create(OpCodes.Ret));

            return rpc;
        }

        public static bool ProcessMethodsValidateTargetRpc(TypeDefinition td, MethodDefinition md, CustomAttribute ca)
        {
            const string targetPrefix = "Target";
            int prefixLen = targetPrefix.Length;

            if (md.Name.Length > prefixLen && md.Name.Substring(0, prefixLen) != targetPrefix)
            {
                Log.Error("Target Rpc function [" + td.FullName + ":" + md.Name + "] doesnt have 'Target' prefix");
                Weaver.WeavingFailed = true;
                return false;
            }

            if (md.IsStatic)
            {
                Log.Error("TargetRpc function [" + td.FullName + ":" + md.Name + "] cant be a static method");
                Weaver.WeavingFailed = true;
                return false;
            }

            if (!NetworkBehaviourProcessor.ProcessMethodsValidateFunction(td, md, "Target Rpc"))
            {
                return false;
            }

            // validate
            return NetworkBehaviourProcessor.ProcessMethodsValidateParameters(td, md, ca, "Target Rpc");
        }
    }
}