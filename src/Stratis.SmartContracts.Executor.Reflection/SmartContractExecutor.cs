﻿using System;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.SmartContracts.Core;
using Stratis.SmartContracts.Core.State;
using Stratis.SmartContracts.Core.State.AccountAbstractionLayer;
using Stratis.SmartContracts.Core.Validation;
using Stratis.SmartContracts.Executor.Reflection.Compilation;
using Block = Stratis.SmartContracts.Core.Block;

namespace Stratis.SmartContracts.Executor.Reflection
{
    /// <summary>
    /// Executes the smart contract code, be it a OP_CREATECONTRACT or OP_CALLCONTRACT
    /// </summary>
    public abstract class SmartContractExecutor : ISmartContractExecutor
    {
        protected readonly ISmartContractTransactionContext transactionContext;
        protected readonly SmartContractCarrier carrier;
        protected readonly IGasMeter gasMeter;
        protected readonly Network network;
        protected readonly IContractStateRepository stateSnapshot;
        protected readonly SmartContractValidator validator;
        protected readonly IKeyEncodingStrategy keyEncodingStrategy;
        private readonly ILogger logger;
        protected readonly ILoggerFactory loggerFactory;

        internal ISmartContractExecutionResult Result { get; set; }


        protected SmartContractExecutor(
            IKeyEncodingStrategy keyEncodingStrategy,
            ILoggerFactory loggerFactory,
            Network network,
            IContractStateRepository stateSnapshot,
            ISmartContractTransactionContext transactionContext,
            SmartContractValidator validator)
        {
            this.carrier = SmartContractCarrier.Deserialize(transactionContext);
            this.gasMeter = new GasMeter(this.carrier.GasLimit);
            this.keyEncodingStrategy = keyEncodingStrategy;
            this.loggerFactory = loggerFactory;
            this.logger = loggerFactory.CreateLogger(this.GetType());
            this.network = network;
            this.stateSnapshot = stateSnapshot.StartTracking();
            this.transactionContext = transactionContext;
            this.validator = validator;
        }

        public ISmartContractExecutionResult Execute()
        {
            try
            {
                PreExecute();
                OnExecute();
                PostExecute();
            }
            catch (Exception unhandled)
            {
                this.logger.LogError("An unhandled exception occurred {0}", unhandled.Message);
                if (unhandled.InnerException != null)
                    this.logger.LogError("{0}", unhandled.InnerException.Message);
            }

            return this.Result;
        }

        /// <summary>
        /// Any logic that should happen before we start contract execution and/or validation
        /// happens here.
        /// <para>
        /// A base fee should be spent before contract execution starts.
        /// </para>
        /// </summary>
        private void PreExecute()
        {
            this.gasMeter.Spend((Gas)GasPriceList.BaseCost);
        }

        public abstract void OnExecute();


        /// <summary>
        /// Any logic that should happen after contract execution has taken place happens here.
        /// </summary>
        private void PostExecute()
        {
            if (this.Result.Revert)
            {
                // Send back funds
                if (this.carrier.Value > 0)
                {
                    this.Result.InternalTransaction = CreateRefundTransaction();
                }

                this.stateSnapshot.Rollback();
            }
            else
            {
                uint160 contractAddress = this.carrier.ContractAddress ?? this.carrier.GetNewContractAddress();

                // If contract received no funds and made no transfers, do nothing.
                if (this.carrier.Value == 0 
                    && !this.Result.InternalTransfers.Any())
                {

                }
                // If contract had no balance, received funds, but made no transfers, assign the current UTXO.
                else if (this.stateSnapshot.GetUnspent(this.carrier.ContractAddress ?? this.carrier.GetNewContractAddress()) == null
                         && this.carrier.Value > 0 
                         && !this.Result.InternalTransfers.Any())
                {
                    this.stateSnapshot.SetUnspent(contractAddress, new ContractUnspentOutput
                    {
                        Value = this.carrier.Value,
                        Hash = this.carrier.TransactionHash,
                        Nvout = this.carrier.Nvout
                    });
                }
                // All other cases we need a condensing transaction
                else
                {
                    var transactionCondenser = new TransactionCondenser(contractAddress, this.loggerFactory, this.Result.InternalTransfers, this.stateSnapshot, this.network, this.transactionContext);
                    this.Result.InternalTransaction = transactionCondenser.CreateCondensingTransaction();
                }

                this.stateSnapshot.Commit();
            }

            new SmartContractExecutorResultProcessor(this.Result, this.loggerFactory).Process(this.carrier, this.transactionContext.MempoolFee);
        }

        /// <summary>
        /// Should contract execution fail, we need to send the money, that was
        /// sent to contract, back to the contract's sender.
        /// </summary>
        private Transaction CreateRefundTransaction()
        {
            var tx = new Transaction();
            // Input from contract call
            var outpoint = new OutPoint(this.transactionContext.TransactionHash, this.transactionContext.Nvout);
            tx.AddInput(new TxIn(outpoint, new Script(OpcodeType.OP_SPEND)));
            // Refund output
            Script script = PayToPubkeyHashTemplate.Instance.GenerateScriptPubKey(new KeyId(this.transactionContext.Sender));
            var txOut = new TxOut(new Money(this.transactionContext.TxOutValue), script);
            tx.Outputs.Add(txOut);
            return tx;
        }

        internal void LogExecutionContext(ILogger logger, IBlock block, IMessage message, uint160 contractAddress, SmartContractCarrier carrier)
        {
            var builder = new StringBuilder();

            builder.Append(string.Format("{0}:{1},{2}:{3},", nameof(block.Coinbase), block.Coinbase, nameof(block.Number), block.Number));
            builder.Append(string.Format("{0}:{1},", nameof(contractAddress), contractAddress.ToAddress(this.network)));
            builder.Append(string.Format("{0}:{1},", nameof(carrier.GasPrice), carrier.GasPrice));
            builder.Append(string.Format("{0}:{1},{2}:{3},{4}:{5},{6}:{7}", nameof(message.ContractAddress), message.ContractAddress, nameof(message.GasLimit), message.GasLimit, nameof(message.Sender), message.Sender, nameof(message.Value), message.Value));

            if (carrier.MethodParameters != null && carrier.MethodParameters.Length > 0)
                builder.Append(string.Format(",{0}:{1}", nameof(carrier.MethodParameters), carrier.MethodParameters));

            logger.LogTrace("{0}", builder.ToString());
        }
    }

    public sealed class CreateSmartContract : SmartContractExecutor
    {
        private readonly ILogger logger;

        public CreateSmartContract(
            IKeyEncodingStrategy keyEncodingStrategy,
            ILoggerFactory loggerFactory,
            Network network,
            IContractStateRepository stateSnapshot,
            ISmartContractTransactionContext transactionContext,
            SmartContractValidator validator)
            : base(keyEncodingStrategy, loggerFactory, network, stateSnapshot, transactionContext, validator)
        {
            this.logger = loggerFactory.CreateLogger(this.GetType());
        }

        public override void OnExecute()
        {
            this.logger.LogTrace("()");

            // Create a new address for the contract.
            uint160 newContractAddress = this.carrier.GetNewContractAddress();

            // Create an account for the contract in the state repository.
            this.stateSnapshot.CreateAccount(newContractAddress);

            // Decompile the contract execution code and validate it.
            SmartContractDecompilation decompilation = SmartContractDecompiler.GetModuleDefinition(this.carrier.ContractExecutionCode);
            SmartContractValidationResult validation = this.validator.Validate(decompilation);

            // If validation failed, refund the sender any remaining gas.
            if (!validation.IsValid)
            {
                this.Result = SmartContractExecutionResult.ValidationFailed(this.carrier, validation);
                this.logger.LogTrace("(-)[CONTRACT_VALIDATION_FAILED]");
                return;
            }

            var block = new Block(this.transactionContext.BlockHeight, this.transactionContext.CoinbaseAddress.ToAddress(this.network));
            var executionContext = new SmartContractExecutionContext
            (
                block,
                new Message(
                    newContractAddress.ToAddress(this.network),
                    this.carrier.Sender.ToAddress(this.network),
                    this.carrier.Value,
                    this.carrier.GasLimit
                ),
                newContractAddress,
                this.carrier.GasPrice,
                this.carrier.MethodParameters
            );

            LogExecutionContext(this.logger, block, executionContext.Message, newContractAddress, this.carrier);

            IPersistenceStrategy persistenceStrategy = new MeteredPersistenceStrategy(this.stateSnapshot, this.gasMeter, new BasicKeyEncodingStrategy());
            var persistentState = new PersistentState(persistenceStrategy, newContractAddress, this.network);

            // TODO push TXExecutorFactory to DI
            var vm = new ReflectionVirtualMachine(new InternalTransactionExecutorFactory(this.keyEncodingStrategy, this.loggerFactory, this.network), this.loggerFactory, persistentState, this.stateSnapshot);

            // Push internal tx executor and getbalance down into VM
            this.Result = vm.Create(this.carrier.ContractExecutionCode, executionContext, this.gasMeter);

            if (this.Result.Revert)
            {
                this.logger.LogTrace("(-)[CONTRACT_EXECUTION_FAILED]");
                return;
            }

            this.Result.NewContractAddress = newContractAddress;

            this.stateSnapshot.SetCode(newContractAddress, this.carrier.ContractExecutionCode);

            this.logger.LogTrace("(-):{0}={1}", nameof(newContractAddress), newContractAddress);
        }
    }

    public sealed class CallSmartContract : SmartContractExecutor
    {
        private readonly ILogger logger;

        public CallSmartContract(
            IKeyEncodingStrategy keyEncodingStrategy,
            ILoggerFactory loggerFactory,
            Network network,
            IContractStateRepository stateSnapshot,
            ISmartContractTransactionContext transactionContext,
            SmartContractValidator validator)
            : base(keyEncodingStrategy, loggerFactory, network, stateSnapshot, transactionContext, validator)
        {
            this.logger = loggerFactory.CreateLogger(this.GetType());
        }

        public override void OnExecute()
        {
            this.logger.LogTrace("()");

            // Get the contract code (dll) from the repository.
            byte[] contractExecutionCode = this.stateSnapshot.GetCode(this.carrier.ContractAddress);
            if (contractExecutionCode == null)
            {
                this.Result = SmartContractExecutionResult.ContractDoesNotExist(this.carrier);
                return;
            }

            // Execute the call to the contract.
            this.Result = this.CreateContextAndExecute(this.carrier.ContractAddress, contractExecutionCode, this.carrier.MethodName);
        }

        private ISmartContractExecutionResult CreateContextAndExecute(uint160 contractAddress, byte[] contractCode, string methodName)
        {
            this.logger.LogTrace("()");

            var block = new Block(this.transactionContext.BlockHeight, this.transactionContext.CoinbaseAddress.ToAddress(this.network));
            var executionContext = new SmartContractExecutionContext
            (
                block,
                new Message(
                    contractAddress.ToAddress(this.network),
                    this.carrier.Sender.ToAddress(this.network),
                    this.carrier.Value,
                    this.carrier.GasLimit
                ),
                contractAddress,
                this.carrier.GasPrice,
                this.carrier.MethodParameters
            );

            LogExecutionContext(this.logger, block, executionContext.Message, contractAddress, this.carrier);

            IPersistenceStrategy persistenceStrategy = new MeteredPersistenceStrategy(this.stateSnapshot, this.gasMeter, this.keyEncodingStrategy);
            var persistentState = new PersistentState(persistenceStrategy, contractAddress, this.network);

            var vm = new ReflectionVirtualMachine(new InternalTransactionExecutorFactory(this.keyEncodingStrategy, this.loggerFactory, this.network), this.loggerFactory, persistentState, this.stateSnapshot);
            ISmartContractExecutionResult result = vm.ExecuteMethod(
                contractCode,
                methodName,
                executionContext,
                this.gasMeter);

            this.logger.LogTrace("(-)");

            return result;
        }
    }
}