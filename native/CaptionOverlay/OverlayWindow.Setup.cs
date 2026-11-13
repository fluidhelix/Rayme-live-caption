using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Rayme.Overlay;

internal sealed partial class OverlayWindow {
    TextBlock? setupMessage,activeModel;
    TextBox? modelPath;
    ProgressBar? setupProgress;
    Button? useModel,downloadModel,retrySetup;
    RadioButton? chooseDownload,chooseLocal;
    FrameworkElement? speechSection;
    bool setupShown,setupRequired;
    JsonElement? setupState;
    void HandleSetup(string json){
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        string? type=root.GetProperty("type").GetString();JsonElement data;
        if(type=="setup")data=root;else if(type=="snapshot"&&root.TryGetProperty("setup",out data)){}else return;
        setupState=data.Clone();setupRequired=data.GetProperty("required").GetBoolean();
        if(data.TryGetProperty("processingMode",out var mode))requestedMode=mode.GetString()??"auto";UpdateProcessingControls();
        if(!quitRequested){connectionText=setupRequired?"Speech setup needed":"Connected";UpdateConnection();}
        UpdateSetup();
        var view=SpeechSetupView.Read(setupState);
        if(setupRequired&&!setupShown&&(view.Phase=="choose"||view.Phase=="error")){setupShown=true;OpenSetup();}
    }
    void OpenSetup(){OpenControls();Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(()=>speechSection?.BringIntoView()));}
    void AddSpeechSettings(Panel panel){
        var body=new StackPanel();speechSection=body;
        activeModel=Theme.Text("Currently active model: Not loaded",16,"#F2F2F2",true);Theme.Accessible(activeModel,"Currently active model");body.Children.Add(activeModel);
        setupMessage=Theme.Text("Checking speech setup…",13,"#DDDDDD");Theme.Accessible(setupMessage,"Speech setup status");setupMessage.Margin=new Thickness(0,8,0,8);body.Children.Add(setupMessage);
        setupProgress=new ProgressBar{Minimum=0,Maximum=100,Height=6,Margin=new Thickness(0,0,0,14)};body.Children.Add(setupProgress);
        chooseDownload=new RadioButton{Content=Theme.Text("Download recommended Whisper Large-v3-Turbo",14,strong:true),GroupName="model-choice",Margin=new Thickness(0,4,0,8)};Theme.Accessible(chooseDownload,"Choose recommended model download");body.Children.Add(chooseDownload);
        body.Children.Add(Theme.Text("1.63 GB download · allow 2.2 GB disk space and 4 GB free RAM. Download begins only when you press Download.",12,"#B9B9B9"));
        downloadModel=Theme.Button("Download Large-v3-Turbo",async(_,_)=>await Send(new{type="setup",action="download",model="large-v3-turbo"}),"\uE896");downloadModel.Margin=new Thickness(0,10,0,12);body.Children.Add(downloadModel);
        chooseLocal=new RadioButton{Content=Theme.Text("Use an existing compatible local Whisper model",14,strong:true),GroupName="model-choice",Margin=new Thickness(0,4,0,8)};Theme.Accessible(chooseLocal,"Choose existing local model");body.Children.Add(chooseLocal);
        modelPath=new TextBox{Name="ModelPath",MinHeight=38,ToolTip="A complete whisper.cpp ggml .bin model, not GGUF"};Theme.Accessible(modelPath,"Existing model path");body.Children.Add(modelPath);
        var actions=new WrapPanel{Margin=new Thickness(0,10,0,0)};
        actions.Children.Add(Theme.Button("Browse…",(_,_)=>{var pick=new OpenFileDialog{Title="Choose a Whisper ggml model",Filter="Whisper ggml models (*.bin)|*.bin|All files (*.*)|*.*",CheckFileExists=true};if(pick.ShowDialog(controls)==true){chooseLocal.IsChecked=true;modelPath.Text=pick.FileName;}},"\uE8B7"));
        useModel=Theme.Button("Use this model",async(_,_)=>await Send(new{type="setup",action="select",path=modelPath.Text}),style:"PrimaryButton");useModel.Name="UseModel";actions.Children.Add(useModel);body.Children.Add(actions);
        retrySetup=Theme.Button("Retry runtime check",async(_,_)=>await Send(new{type="setup",action="retry"}),"\uE72C");body.Children.Add(retrySetup);
        chooseLocal.Checked+=(_,_)=>UpdateSetup();chooseDownload.Checked+=(_,_)=>UpdateSetup();modelPath.TextChanged+=(_,_)=>UpdateSetup();
        panel.Children.Add(Theme.Card(body));AddProcessingControls(panel);
        var details=new Expander{Header="Model, privacy and hardware details",Margin=new Thickness(0,0,0,10),Content=Theme.Text("Recognition stays on this PC; no private audio is recorded. Completed valid models work offline. Interrupted downloads can be retried and are verified against the pinned upstream hash. Large-v3-Turbo uses official OpenAI weights converted by whisper.cpp (MIT License). Auto prefers compatible NVIDIA CUDA, otherwise CPU; NVIDIA mode needs a compatible driver. Requested mode alone does not prove GPU inference. The actual backend and verification evidence appear above. Sources and pause state return after a successful mode change.",12,"#B9B9B9")};panel.Children.Add(details);
        UpdateSetup();
    }
    void UpdateSetup(){
        if(controls==null||setupMessage==null)return;
        var view=SpeechSetupView.Read(setupState);
        activeModel!.Text="Currently active model: "+(view.ActiveModel.Length>0?view.ActiveModel:"Not loaded");activeModel.ToolTip=view.ActiveModel;
        setupMessage.Text=view.Message;System.Windows.Automation.AutomationProperties.SetHelpText(setupMessage,view.Message);
        double downloaded=0,total=0;
        if(setupState is {} data){if(data.TryGetProperty("downloaded",out var n))downloaded=n.GetDouble();if(data.TryGetProperty("total",out var t))total=t.GetDouble();
            if(modelPath!.Text.Length==0&&data.TryGetProperty("selectedModel",out var selected)&&selected.ValueKind==JsonValueKind.Object&&selected.TryGetProperty("path",out var path))modelPath.Text=path.GetString()??"";
        }
        if(view.Phase=="downloading")setupMessage.Text+=$"\n{downloaded/1000000:0} / {total/1000000:0} MB";
        setupProgress!.Value=total>0?Math.Clamp(downloaded*100/total,0,100):0;setupProgress.IsIndeterminate=view.Busy&&view.Phase!="downloading";
        useModel!.IsEnabled=!view.Busy&&chooseLocal!.IsChecked==true&&!string.IsNullOrWhiteSpace(modelPath!.Text);
        downloadModel!.IsEnabled=!view.Busy&&chooseDownload!.IsChecked==true;retrySetup!.IsEnabled=!view.Busy;
        chooseLocal!.IsEnabled=chooseDownload!.IsEnabled=modelPath!.IsEnabled=!view.Busy;
    }
}
