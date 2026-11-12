using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Rayme.Overlay;

internal sealed partial class OverlayWindow {
    JsonElement? processingState;
    readonly List<ComboBox> processingChoices=[];
    readonly List<TextBlock> processingLabels=[];
    bool processingSync;
    string requestedMode="auto";
    void AddProcessingControls(Panel panel){
        var row=new StackPanel();
        row.Children.Add(Theme.Text("Processing mode",14,strong:true));
        row.Children.Add(Theme.Text("Auto prefers compatible NVIDIA CUDA. Switching stops capture and reloads the model; enable sources again afterward.",12,"#B9C7DC"));
        var select=new ComboBox { ItemsSource=new[]{new Choice("auto","Auto — prefer compatible NVIDIA GPU"),new Choice("cuda","NVIDIA GPU (CUDA)"),new Choice("cpu","CPU")},DisplayMemberPath="Name",SelectedValuePath="Id",SelectedValue=requestedMode,Margin=new Thickness(0,10,0,8) };
        Theme.Accessible(select,"Speech processing mode");processingChoices.Add(select);
        select.Unloaded+=(_,_)=>processingChoices.Remove(select);
        select.SelectionChanged+=async(_,_)=>{if(!processingSync&&select.SelectedValue is string mode)await Send(new{type="setup",action="mode",mode});};
        row.Children.Add(select);var actual=Theme.Text("Actual device: not initialized. GPU acceleration is not inferred from the selected mode.",12,"#D5E5F5");Theme.Accessible(actual,"Actual speech processing device");processingLabels.Add(actual);actual.Unloaded+=(_,_)=>processingLabels.Remove(actual);row.Children.Add(actual);panel.Children.Add(Theme.Card(row));UpdateProcessingControls();
    }
    void HandleProcessing(string json){
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;var type=root.GetProperty("type").GetString();JsonElement data;
        if(type=="processing")data=root;else if(type=="snapshot"&&root.TryGetProperty("processing",out data)){}else return;
        processingState=data.Clone();UpdateProcessingControls();
    }
    void UpdateProcessingControls(){
        processingSync=true;
        try{
            foreach(var choice in processingChoices){choice.SelectedValue=requestedMode;choice.IsEnabled=setupState is not {} setup||!(setup.GetProperty("phase").GetString() is "loading" or "downloading" or "verifying" or "switching");}
            string text="Actual device: not initialized. GPU acceleration is not inferred from the selected mode.";
            if(processingState is {} state){string name=state.GetProperty("name").GetString()??"Unknown";string actual=state.GetProperty("actual").GetString()??"not-started";bool verified=state.TryGetProperty("verified",out var proof)&&proof.GetBoolean();string message=state.TryGetProperty("message",out var detail)?detail.GetString()??"":"";
                text=$"Actual device: {name} ({actual.ToUpperInvariant()})\n{message}"+(actual=="cuda"&&!verified?"\nGPU inference/activity remains unverified.":"");}
            foreach(var label in processingLabels){label.Text=text;System.Windows.Automation.AutomationProperties.SetHelpText(label,text);}
        }finally{processingSync=false;}
    }
}
