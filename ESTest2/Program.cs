using System;
using System.Collections.Generic;
using System.Linq;

namespace ESTest2
{
    public interface @Command { }

    public interface @Event {}

    public interface @State {}

    public struct @Error {
        public string Message { get; }
        public static readonly Error None = new Error { };
        private Error(string message) {
            this.Message = message;
        }
        public static @Error WithMessage(string message) {
            return new Error(message);
        }
    }

    public interface Domain {
        @State Apply(@State state, @Event evnt);
        (@Error? error, IEnumerable<@Event> events) Decide(@Command cmd, @State state);
        @State Initial { get; }
    }

    public interface @EventStore {
        Error? AppendToStream(Guid id, int version, IEnumerable<@Event> events);
        (Error? error, IEnumerable<@Event> events) ReadFromStream(Guid id);
    }

    public static class DomainHelper
    {
        public static readonly IEnumerable<Event> NoEvents = Enumerable.Empty<Event>();

        public static (@State state, int version) Replay(this Domain domain, @State initial, IEnumerable<@Event> events)
        {
            var version = -1;
            var state = initial;
            foreach (var evnt in events)
            {
                state = domain.Apply(state, evnt);
                version++;
            }
            return (state, version);
        }

        public static Error? HandleCommand(this Domain domain, EventStore store, Guid id, Command cmd)
        {
            var current = Query(domain, store, id);
            if (current.error.HasValue) return current.error;
            var result = domain.Decide(cmd, current.state);
            if (result.error.HasValue) return result.error;
            return store.AppendToStream(id, current.version, result.events);
        }


        public static (Error? error, State state, int version) Query(this Domain domain, EventStore store, Guid id)
        {
            var state = domain.Initial;
            var stream = store.ReadFromStream(id);
            if (stream.error.HasValue) return (stream.error, state, -1);
            var current = domain.Replay(state, stream.events);
            return (null, current.state, current.version);
        }

        public static T Query<T>(this Domain domain, EventStore store, Guid id) where T : State {
            var result = Query(domain, store, id);
            if (result.error.HasValue) throw new InvalidOperationException(result.error.Value.Message);
            return (T)result.state;
        }

        public static (@Error? error, IEnumerable<@Event> events) Indecision => (Error.None, NoEvents);

        public static (@Error? error, IEnumerable<@Event> events) Events(params @Event[] args) => (null, args);

        public static (@Error? error, IEnumerable<@Event> events) ErrorMessage(string message) => (Error.WithMessage(message), Enumerable.Empty<Event>());

        public static (@State state, @Event evnt) OnEvent(@State state, @Event evnt) => (state, evnt);

        public static (@State state, @Event evnt) When<TState, TEvent>(this (@State state, @Event evnt) onEvent, Func<TState, TEvent, State> callback) where TState : State where TEvent : Event
        {
            if (onEvent.state is TState && onEvent.evnt is TEvent)
            {
                var newState = callback((TState)onEvent.state, (TEvent)onEvent.evnt);
                return (newState, onEvent.evnt);
            }
            return onEvent;
        }

        public static @State State(this (@State state, @Event evnt) onEvent) => onEvent.state;

        public static (@Command cmd, @State state, Func<(@Error? error, IEnumerable<@Event> events)>) OnCommand(@Command cmd, @State state) => (cmd, state, null);

        public static (@Command cmd, @State state, Func<(@Error? error, IEnumerable<@Event> events)>) When<TCommand, TState>(this (@Command cmd, @State state, Func<(@Error? error, IEnumerable<@Event> events)> result) onCommand, Func<TCommand, TState, (@Error? error, IEnumerable<@Event> events)> callback)
            where TCommand : Command
            where TState : State
        {
            if (null == onCommand.result && onCommand.cmd is TCommand && onCommand.state is TState)
            {
                (Error? error, IEnumerable<Event> events) result() => callback((TCommand)onCommand.cmd, (TState)onCommand.state);
                return (onCommand.cmd, onCommand.state, result);
            }
            return onCommand;
        }

        public static (@Error? error, IEnumerable<@Event> events) Result(this (@Command cmd, @State state, Func<(@Error? error, IEnumerable<@Event> events)> result) onCommand) => null != onCommand.result ? onCommand.result() : Indecision;

        public static (@Command cmd, @State state, Func<(@Error? error, IEnumerable<@Event> events)>) UnrecgonizedCommand(this (@Command cmd, @State state, Func<(@Error? error, IEnumerable<@Event> events)> result) onCommand) {
            if (null == onCommand.result)
            {
                (Error? error, IEnumerable<Event> events) result() => ErrorMessage($"{onCommand.cmd} not handled for {onCommand.state}");
                return (onCommand.cmd, onCommand.state, result);
            }
            return onCommand;
        }

        class AccountDomain : Domain
        {
            public struct AccountState : State
            {
                public string AccountName { get; }
                public decimal Balance { get; }
                public AccountState(string accountName, decimal balance)
                {
                    this.AccountName = accountName;
                    this.Balance = balance;
                }
            }

            public State Initial { get => new AccountState(string.Empty, 0); }

            public struct CreateOnlineAccount : Command
            {
                public string AccountName { get; }
                public CreateOnlineAccount(string accountName)
                {
                    this.AccountName = accountName;
                }
            }

            public struct MakeDeposit : Command {
                public decimal Amount { get; }
                public MakeDeposit(decimal amount) { this.Amount = amount; }
            }

            public struct OnlineAccountCreated : Event
            {
                public string AccountName { get; }
                public OnlineAccountCreated(string accountName)
                {
                    this.AccountName = accountName;
                }
            }

            public struct DepositMade : Event
            {
                public decimal Amount { get; }
                public DepositMade(decimal amount) { this.Amount = amount; }
            }



            public static (@Error? error, IEnumerable<@Event> events) OnMakeDepositCommand(MakeDeposit cmd, AccountState state)
            {
                if (cmd.Amount < 0)
                {
                    DomainHelper.ErrorMessage("deposit amount must be positive");
                }
                return DomainHelper.Events(new DepositMade(cmd.Amount));
            }

            public static (@Error? error, IEnumerable<@Event> events) OnCreateOnlineAccountCommand(CreateOnlineAccount cmd, AccountState state)
            {
                return DomainHelper.Events(new OnlineAccountCreated(cmd.AccountName));
            }

            public (@Error? error, IEnumerable<@Event> events) Decide(@Command cmd, @State state)
            {
                return DomainHelper
                    .OnCommand(cmd, state)
                    .When<CreateOnlineAccount, AccountState>(OnCreateOnlineAccountCommand)
                    .When<MakeDeposit, AccountState>(OnMakeDepositCommand)
                    .UnrecgonizedCommand()
                    .Result();
            }

            public State Apply(@State state, @Event evnt)
            {
                return DomainHelper
                    .OnEvent(state, evnt)
                    .When<AccountState, OnlineAccountCreated>((s, e) => new AccountState(e.AccountName, 0))
                    .When<AccountState, DepositMade>((s, e) => new AccountState(s.AccountName, s.Balance + e.Amount))
                    .State();
            }
        }

        class InMemoryEventStore : EventStore {
            public readonly IDictionary<Guid, (IEnumerable<Event> events, int version)> entries = new Dictionary<Guid, (IEnumerable<Event> events, int version)>();

            public Error? AppendToStream(Guid id, int version, IEnumerable<@Event> events){
                if (!entries.ContainsKey(id)){
                    entries.Add(id,(DomainHelper.NoEvents, -1));
                }
                var current = entries[id];
                if (version != current.version) return Error.WithMessage("Version Mismatch");
                foreach(var evnt in events) {
                    current = (current.events.Append(evnt).ToArray(), current.version + 1);
                }
                entries[id] = current;
                return null;
            }

            public (Error? error, IEnumerable<@Event> events) ReadFromStream(Guid id) {
                if (!entries.ContainsKey(id))
                {
                    return (null, DomainHelper.NoEvents);
                }
                return (null, entries[id].events);
            }
        }


        class Program
        {
            static void Main(string[] args)
            {
                var domain = new AccountDomain();
                EventStore store = new InMemoryEventStore();

                var id = Guid.NewGuid();

                var error = domain.HandleCommand(store, id, new AccountDomain.CreateOnlineAccount("Matts Account"));

                error = domain.HandleCommand(store, id, new AccountDomain.MakeDeposit(100));

                var query = domain.Query<AccountDomain.AccountState>(store, id);

                System.Diagnostics.Debug.Assert(100 == query.Balance);

            }
        }
    }
}
