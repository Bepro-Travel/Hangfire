﻿// This file is part of HangFire.
// Copyright © 2013-2014 Sergey Odinokov.
// 
// HangFire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// HangFire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with HangFire. If not, see <http://www.gnu.org/licenses/>.

using System;
using HangFire.Common.States;
using HangFire.Server.Performing;
using HangFire.States;
using HangFire.Storage;

namespace HangFire.Server
{
    public class ProcessingJob : IProcessingJob
    {
        private readonly IStorageConnection _connection;
        private readonly IStateMachineFactory _stateMachineFactory;

        public ProcessingJob(
            IStorageConnection connection,
            IStateMachineFactory stateMachineFactory,
            string jobId,
            string queue)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            if (stateMachineFactory == null) throw new ArgumentNullException("stateMachineFactory");
            if (jobId == null) throw new ArgumentNullException("jobId");
            if (queue == null) throw new ArgumentNullException("queue");

            _connection = connection;
            _stateMachineFactory = stateMachineFactory;
            JobId = jobId;
            Queue = queue;
        }

        public string JobId { get; private set; }
        public string Queue { get; private set; }

        public void Process(WorkerContext context, IJobPerformanceProcess process)
        {
            if (context == null) throw new ArgumentNullException("context");
            if (process == null) throw new ArgumentNullException("process");

            var stateMachine = _stateMachineFactory.Create(_connection);
            var processingState = new ProcessingState(context.ServerName);

            if (!stateMachine.TryToChangeState(
                JobId, 
                processingState, 
                new [] { EnqueuedState.StateName, ProcessingState.StateName }))
            {
                return;
            }

            // Checkpoint #3. Job is in the Processing state. However, there are
            // no guarantees that it was performed. We need to re-queue it even
            // it was performed to guarantee that it was performed AT LEAST once.
            // It will be re-queued after the JobTimeout was expired.

            State state;

            try
            {
                var jobData = _connection.GetJobData(JobId);
                jobData.EnsureLoaded();

                var performContext = new PerformContext(context, _connection, JobId, jobData.Job);

                process.Run(performContext, jobData.Job);

                state = new SucceededState();
            }
            catch (JobPerformanceException ex)
            {
                state = new FailedState(ex.InnerException)
                {
                    Reason = ex.Message
                };
            }
            catch (Exception ex)
            {
                state = new FailedState(ex)
                {
                    Reason = "Internal HangFire Server exception occurred. Please, report it to HangFire developers."
                };
            }

            // Ignore return value, because we should not do
            // anything when current state is not Processing.
            stateMachine.TryToChangeState(JobId, state, new [] { ProcessingState.StateName });
        }

        public void Dispose()
        {
            _connection.DeleteJobFromQueue(JobId, Queue);
        }
    }
}
