using Core.SharedModel;


namespace Core.Contracts
{
    public interface IControllerRpcService
    {
        void InvokeRPC(string methodName, NetworkTarget target, params object[] parameters);
    }
}