﻿/*
    This file is part of the d# project.
    Copyright (c) 2016-2018 ei8
    Authors: ei8
     This program is free software; you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License version 3
    as published by the Free Software Foundation with the addition of the
    following permission added to Section 15 as permitted in Section 7(a):
    FOR ANY PART OF THE COVERED WORK IN WHICH THE COPYRIGHT IS OWNED BY
    EI8. EI8 DISCLAIMS THE WARRANTY OF NON INFRINGEMENT OF THIRD PARTY RIGHTS
     This program is distributed in the hope that it will be useful, but
    WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
    or FITNESS FOR A PARTICULAR PURPOSE.
    See the GNU Affero General Public License for more details.
    You should have received a copy of the GNU Affero General Public License
    along with this program; if not, see http://www.gnu.org/licenses or write to
    the Free Software Foundation, Inc., 51 Franklin Street, Fifth Floor,
    Boston, MA, 02110-1301 USA, or download the license from the following URL:
    https://github.com/ei8/cortex-diary/blob/master/LICENSE
     The interactive user interfaces in modified source and object code versions
    of this program must display Appropriate Legal Notices, as required under
    Section 5 of the GNU Affero General Public License.
     You can be released from the requirements of the license by purchasing
    a commercial license. Buying such a license is mandatory as soon as you
    develop commercial activities involving the d# software without
    disclosing the source code of your own applications.
     For more information, please contact ei8 at this address: 
     support@ei8.works
 */

using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using Splat;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using works.ei8.Cortex.Diary.Application.Neurons;
using works.ei8.Cortex.Diary.Domain.Model.Neurons;
using works.ei8.Cortex.Diary.Domain.Model.Origin;
using works.ei8.Cortex.Diary.Port.Adapter.UI.ViewModels.Dialogs;
using works.ei8.Cortex.Diary.Port.Adapter.UI.ViewModels.Docking;

namespace works.ei8.Cortex.Diary.Port.Adapter.UI.ViewModels.Neurons
{
    public class NeuronTreePaneViewModel : PaneViewModel, IDisposable
    {
        private readonly INeuronApplicationService neuronApplicationService;
        private readonly INeuronQueryService neuronQueryService;
        private readonly IOriginService originService;
        private readonly ReadOnlyObservableCollection<NeuronViewModelBase> children;
        private readonly IDisposable cleanUp;
        private readonly IStatusService statusService;
        private readonly IDialogService dialogService;
        private bool reloaded;

        public NeuronTreePaneViewModel(INeuronApplicationService neuronApplicationService = null, INeuronQueryService neuronQueryService = null, IOriginService originService = null, 
            IStatusService statusService = null, IDialogService dialogService = null)
        {
            this.neuronApplicationService = neuronApplicationService ?? Locator.Current.GetService<INeuronApplicationService>();
            this.neuronQueryService = neuronQueryService ?? Locator.Current.GetService<INeuronQueryService>();
            this.originService = originService ?? Locator.Current.GetService<IOriginService>();
            this.statusService = statusService ?? Locator.Current.GetService<IStatusService>();
            this.dialogService = dialogService ?? Locator.Current.GetService<IDialogService>();
            this.reloaded = false;

            this.statusService.WhenPropertyChanged(s => s.Message)
                .Subscribe(s => this.StatusMessage = s.Sender.Message);

            bool DefaultPredicate(Node<Neuron, int> node) => node.IsRoot;
            var cache = new SourceCache<Neuron, int>(x => x.Id);

            this.AddCommand = ReactiveCommand.Create<object>(async(parameter) => await this.OnAddClicked(cache, parameter));
            this.SetLayerCommand = ReactiveCommand.Create<object>(async(parameter) => await this.OnSetLayerIdClicked(parameter));
            this.ReloadCommand = ReactiveCommand.Create(async() => await this.OnReloadClicked(cache));

            this.cleanUp = cache.AsObservableCache().Connect()
                .TransformToTree(child => child.CentralId, Observable.Return((Func<Node<Neuron, int>, bool>)DefaultPredicate))
                .Transform(e =>
                    e.Item.Type == RelativeType.Postsynaptic ?
                    (NeuronViewModelBase)(new PostsynapticViewModel(this.avatarUrl, e.Item.Tag, e, cache)) :
                    (NeuronViewModelBase)(new PresynapticViewModel(this.avatarUrl, e.Item.Tag, e, cache)))
                .Bind(out this.children)
                .DisposeMany()
                .Subscribe();

            this.IconSourcePath = @"pack://application:,,,/Dasz;component/images/hierarchy.ico";            
        }

        private async Task OnAddClicked(SourceCache<Neuron, int> cache, object parameter)
        {
            await Helper.SetStatusOnComplete(async () =>
            {
                bool stat = false;
                bool shouldAddOwner = this.reloaded && cache.Count == 0;
                bool addingOwner = false;
                if (shouldAddOwner && (await this.dialogService.ShowDialogYesNo("This Avatar needs to be initialized with an Owner Neuron. Do you wish to continue by creating one?", parameter, out DialogResult yesno)).GetValueOrDefault())
                    addingOwner = true;

                if (
                        (!shouldAddOwner || addingOwner) &&
                        (await this.dialogService.ShowDialogTextInput(
                            addingOwner ? "Enter Owner Name" : "Enter Neuron Tag: ", 
                            this.avatarUrl, 
                            parameter, 
                            out string result
                            )
                        ).GetValueOrDefault() &&
                        await Helper.PromptSimilarExists(this.neuronQueryService, this.dialogService, this.avatarUrl, parameter, result)
                    )
                {
                    Neuron n = new Neuron
                    {
                        Tag = result,
                        Id = Guid.NewGuid().GetHashCode(),
                        NeuronId = Guid.NewGuid().ToString(),
                        Version = 1,
                    };

                    await this.neuronApplicationService.CreateNeuron(
                        this.avatarUrl,
                        n.NeuronId,
                        ViewModels.Helper.CleanForJSON(n.Tag),
                        this.originService.GetAvatarByUrl(this.avatarUrl).LayerId
                        );
                    cache.AddOrUpdate(n);
                    stat = true;
                }
                return stat;
            },
                "Neuron added successfully.",
                this.statusService,
                "Neuron addition cancelled."
                );
        }

        private async Task OnSetLayerIdClicked(object parameter)
        {
            await Helper.SetStatusOnComplete(async() =>
                {
                    bool stat = false;

                    if ((await this.dialogService.ShowDialogSelectNeurons("Select Layer Neuron", this.avatarUrl, parameter, false, out IEnumerable<Neuron> result)).GetValueOrDefault())
                    {
                        this.LayerName = result.First().Tag;
                        this.originService.GetAvatarByUrl(this.avatarUrl).LayerId = result.First().NeuronId;
                        stat = true;
                    }
                    else
                        this.InitLayer();

                    return stat;                    
                },
                "Layer set successfully.",
                this.statusService,
                "Layer set cancelled."
                );
        }

        private void InitLayer()
        {
            this.LayerName = "[Base]";
            this.originService.GetAvatarByUrl(this.avatarUrl).LayerId = Guid.Empty.ToString();
        }

        private async Task OnReloadClicked(SourceCache<Neuron, int> cache)
        {
            await Helper.SetStatusOnComplete(async () =>
                {
                    cache.Clear();
                    var relatives = await this.neuronQueryService.GetNeurons(this.avatarUrl);
                    this.originService.Save(this.avatarUrl);
                    cache.AddOrUpdate(relatives);
                    this.InitLayer();
                    this.reloaded = true;
                    return true;
                },
                "Reload successful.",
                this.statusService
            );
        }

        public ReactiveCommand AddCommand { get; }

        private string layerName;

        public string LayerName
        {
            get => this.layerName;
            set => this.RaiseAndSetIfChanged(ref layerName, value);
        }

        public ReactiveCommand SetLayerCommand { get; }

        private string avatarUrl;

        public string AvatarUrl
        {
            get => this.avatarUrl;
            set => this.RaiseAndSetIfChanged(ref this.avatarUrl, value);
        }

        public ReadOnlyObservableCollection<NeuronViewModelBase> Children => this.children;

        public ReactiveCommand ReloadCommand { get; }

        private string statusMessage;

        public string StatusMessage
        {
            get => this.statusMessage;
            set => this.RaiseAndSetIfChanged(ref this.statusMessage, value);
        }
        
        public void Dispose()
        {
            this.cleanUp.Dispose();
        }
    }
}
