using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Mirror
{
    /// <summary>
    /// Narrow remote-call virtualization hook used by the multiplayer quest layer.
    ///
    /// Mirror still owns the real client/server transport. This helper exposes three
    /// interception points before a Command/ClientRpc/TargetRpc is dispatched, plus a
    /// safe way to execute an already-authorized Command payload on a remote party leader.
    ///
    /// It contains no reference to QuestNetSync or party code so Mirror remains
    /// independent from game assemblies. The game-side PartyQuestVirtualAuthority
    /// installs the delegates at runtime and returns false for every non-quest call.
    /// </summary>
    public static class QuestNetVirtualTransport
    {
        public delegate bool CommandInterceptorDelegate(
            NetworkConnection connection,
            NetworkIdentity identity,
            CommandMessage message,
            int channelId);

        public delegate bool ClientRpcInterceptorDelegate(
            NetworkBehaviour behaviour,
            RpcMessage message,
            bool includeOwner,
            int channelId);

        public delegate bool TargetRpcInterceptorDelegate(
            NetworkBehaviour behaviour,
            NetworkConnection target,
            RpcMessage message,
            int channelId);

        public delegate bool ServerLifecycleInterceptorDelegate(
            NetworkBehaviour behaviour,
            bool starting);

        public static CommandInterceptorDelegate CommandInterceptor;
        public static ClientRpcInterceptorDelegate ClientRpcInterceptor;
        public static TargetRpcInterceptorDelegate TargetRpcInterceptor;
        public static ServerLifecycleInterceptorDelegate ServerLifecycleInterceptor;

        // Mirror's Weaver moves every [Command] body into a protected method named
        // UserCode_<CommandName>. The normal generated InvokeCmd wrapper first checks
        // NetworkServer.active, which is deliberately false on a remote party leader.
        // Instead of falsifying that global flag (which would make unrelated game code
        // think the client is a real server), virtual quest Commands deserialize their
        // original payload and invoke that Weaver-generated user-code method directly.
        // Reflection metadata is cached once per component type/hash.
        static readonly Dictionary<Type, Dictionary<int, MethodInfo>> virtualCommandMethods =
            new Dictionary<Type, Dictionary<int, MethodInfo>>();
        static readonly Dictionary<Type, MethodInfo> closedReaderMethods =
            new Dictionary<Type, MethodInfo>();

        static MethodInfo genericNetworkReaderReadMethod;

        internal static bool TryInterceptServerLifecycle(
            NetworkBehaviour behaviour,
            bool starting)
        {
            ServerLifecycleInterceptorDelegate interceptor = ServerLifecycleInterceptor;
            return interceptor != null && interceptor(behaviour, starting);
        }

        internal static bool TryInterceptCommand(
            NetworkConnection connection,
            NetworkIdentity identity,
            CommandMessage message,
            int channelId)
        {
            CommandInterceptorDelegate interceptor = CommandInterceptor;
            return interceptor != null && interceptor(connection, identity, message, channelId);
        }

        internal static bool TryInterceptClientRpc(
            NetworkBehaviour behaviour,
            Type invokeClass,
            string rpcName,
            NetworkWriter writer,
            int channelId,
            bool includeOwner)
        {
            ClientRpcInterceptorDelegate interceptor = ClientRpcInterceptor;
            if (interceptor == null || behaviour == null || writer == null)
                return false;

            RpcMessage message = new RpcMessage
            {
                netId = behaviour.netId,
                componentIndex = (byte)behaviour.ComponentIndex,
                functionHash = RemoteCalls.RemoteCallHelper.GetMethodHash(invokeClass, rpcName),
                payload = writer.ToArraySegment(),
            };

            return interceptor(behaviour, message, includeOwner, channelId);
        }

        internal static bool TryInterceptTargetRpc(
            NetworkBehaviour behaviour,
            NetworkConnection target,
            Type invokeClass,
            string rpcName,
            NetworkWriter writer,
            int channelId)
        {
            TargetRpcInterceptorDelegate interceptor = TargetRpcInterceptor;
            if (interceptor == null || behaviour == null || writer == null)
                return false;

            RpcMessage message = new RpcMessage
            {
                netId = behaviour.netId,
                componentIndex = (byte)behaviour.ComponentIndex,
                functionHash = RemoteCalls.RemoteCallHelper.GetMethodHash(invokeClass, rpcName),
                payload = writer.ToArraySegment(),
            };

            return interceptor(behaviour, target, message, channelId);
        }

        /// <summary>
        /// Creates a server-shaped connection token for use only inside virtualized
        /// QuestNetSync code on a client. The token is never allowed to send directly;
        /// QuestNetSync TargetRpcs are intercepted before Mirror reaches conn.Send().
        /// </summary>
        public static NetworkConnectionToClient CreateVirtualConnection(
            int connectionId,
            NetworkIdentity identity)
        {
            NetworkConnectionToClient connection = new NetworkConnectionToClient(connectionId);
            connection.identity = identity;
            connection.isReady = true;
            connection.isAuthenticated = true;
            return connection;
        }

        /// <summary>
        /// Executes a woven Command payload on a client after the real server has already
        /// authenticated and routed it to that client's party-leader authority.
        ///
        /// This bypasses only Mirror's generated NetworkServer.active guard by invoking
        /// the Weaver-generated UserCode_Cmd* body directly. Mirror.NetworkServer.active
        /// is NEVER changed, so unrelated systems on the party leader remain ordinary
        /// clients throughout the whole quest operation.
        /// </summary>
        public static bool InvokeCommandOnVirtualServer(
            CommandMessage message,
            NetworkConnectionToClient virtualSender)
        {
            NetworkIdentity identity;
            if (!NetworkClient.spawned.TryGetValue(message.netId, out identity) || identity == null)
            {
                Debug.LogWarning(
                    "[PartyQuestVirtual] Cannot invoke relayed Command because client has no " +
                    "spawned identity netId=" + message.netId + ".");
                return false;
            }

            if (identity.NetworkBehaviours == null ||
                message.componentIndex >= identity.NetworkBehaviours.Length)
            {
                Debug.LogWarning(
                    "[PartyQuestVirtual] Cannot invoke relayed Command because component index " +
                    message.componentIndex + " is invalid for netId=" + message.netId + ".");
                return false;
            }

            NetworkBehaviour component = identity.NetworkBehaviours[message.componentIndex];
            if (component == null)
                return false;

            MethodInfo userCode = FindVirtualCommandMethod(component.GetType(), message.functionHash);
            if (userCode == null)
            {
                Debug.LogError(
                    "[PartyQuestVirtual] Could not find Weaver UserCode command for type=" +
                    component.GetType().FullName + " hash=" + message.functionHash + ".");
                return false;
            }

            try
            {
                using (PooledNetworkReader reader = NetworkReaderPool.GetReader(message.payload))
                {
                    ParameterInfo[] parameters = userCode.GetParameters();
                    object[] arguments = new object[parameters.Length];

                    for (int i = 0; i < parameters.Length; i++)
                    {
                        Type parameterType = parameters[i].ParameterType;

                        // Mirror does not serialize the special Command sender parameter.
                        // The real server already authenticated the sender before relaying.
                        if (typeof(NetworkConnectionToClient).IsAssignableFrom(parameterType))
                        {
                            arguments[i] = virtualSender;
                            continue;
                        }

                        arguments[i] = ReadVirtualCommandArgument(reader, parameterType);
                    }

                    if (reader.Remaining != 0 && Debug.isDebugBuild)
                    {
                        Debug.LogWarning(
                            "[PartyQuestVirtual] Relayed Command payload had " + reader.Remaining +
                            " unread byte(s). method=" + userCode.Name + ".");
                    }

                    userCode.Invoke(component, arguments);
                }

                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                Debug.LogError(
                    "[PartyQuestVirtual] Relayed Command user code failed method=" +
                    userCode.Name + ": " + inner);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    "[PartyQuestVirtual] Relayed Command decode/invocation failed method=" +
                    userCode.Name + ": " + ex);
                return false;
            }
        }

        static MethodInfo FindVirtualCommandMethod(Type componentType, int functionHash)
        {
            if (componentType == null)
                return null;

            Dictionary<int, MethodInfo> byHash;
            if (!virtualCommandMethods.TryGetValue(componentType, out byHash))
            {
                byHash = BuildVirtualCommandMap(componentType);
                virtualCommandMethods[componentType] = byHash;
            }

            MethodInfo method;
            return byHash.TryGetValue(functionHash, out method) ? method : null;
        }

        static Dictionary<int, MethodInfo> BuildVirtualCommandMap(Type componentType)
        {
            Dictionary<int, MethodInfo> result = new Dictionary<int, MethodInfo>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                       BindingFlags.NonPublic;

            MethodInfo[] methods = componentType.GetMethods(flags);
            const string prefix = "UserCode_";

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || !method.Name.StartsWith(prefix + "Cmd", StringComparison.Ordinal))
                    continue;

                string commandName = method.Name.Substring(prefix.Length);
                Type declaringType = method.DeclaringType ?? componentType;
                int hash = RemoteCalls.RemoteCallHelper.GetMethodHash(declaringType, commandName);

                MethodInfo existing;
                if (result.TryGetValue(hash, out existing) && existing != method)
                {
                    Debug.LogError(
                        "[PartyQuestVirtual] Command hash collision while building virtual map: " +
                        existing.Name + " and " + method.Name + " hash=" + hash + ".");
                    continue;
                }

                result[hash] = method;
            }

            return result;
        }

        static object ReadVirtualCommandArgument(NetworkReader reader, Type parameterType)
        {
            MethodInfo readMethod;
            if (!closedReaderMethods.TryGetValue(parameterType, out readMethod))
            {
                MethodInfo generic = GetGenericNetworkReaderReadMethod();
                if (generic == null)
                    throw new MissingMethodException("Mirror.NetworkReader.Read<T>() was not found.");

                readMethod = generic.MakeGenericMethod(parameterType);
                closedReaderMethods[parameterType] = readMethod;
            }

            return readMethod.Invoke(reader, null);
        }

        static MethodInfo GetGenericNetworkReaderReadMethod()
        {
            if (genericNetworkReaderReadMethod != null)
                return genericNetworkReaderReadMethod;

            MethodInfo[] methods = typeof(NetworkReader).GetMethods(
                BindingFlags.Instance | BindingFlags.Public);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name == "Read" && method.IsGenericMethodDefinition &&
                    method.GetParameters().Length == 0 && method.GetGenericArguments().Length == 1)
                {
                    genericNetworkReaderReadMethod = method;
                    break;
                }
            }

            return genericNetworkReaderReadMethod;
        }

        public static void ClearInterceptors()
        {
            CommandInterceptor = null;
            ClientRpcInterceptor = null;
            TargetRpcInterceptor = null;
            ServerLifecycleInterceptor = null;
            virtualCommandMethods.Clear();
            closedReaderMethods.Clear();
            genericNetworkReaderReadMethod = null;
        }
    }
}
