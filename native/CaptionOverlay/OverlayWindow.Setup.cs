using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Rayme.Overlay;

internal sealed partial class OverlayWindow {
    Window? setupWindow;
    TextBlock? setupMessage;
    TextBox? modelPath;
    ProgressBar? setupProgress;
    Button? useModel,downloadModel,retrySetup;
    bool setupShown,setupRequired;
    JsonElement? setupState;
    void HandleSetup(string json){
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        string? type=root.GetProperty("type").GetString();JsonElement data;
        if(type=="setup")data=root;
        else if(type=="snapshot"&&root.TryGetProperty("setup",out data)){}
        else return;
        setupState=data.Clone();setupRequired=data.GetProperty("required").GetBoolean();
        if(data.TryGetProperty("processingMode",out var mode))requestedMode=mode.GetString()??"auto";UpdateProcessingControls();
        string phase=data.GetProperty("phase").GetString()??"";
        waiting.Text=setupRequired?"Complete speech setup to begin.\nOpen Controls → Speech model setup.":"Waiting for conversation…\nOpen Controls to enable applications.";
        if(!quitRequested){connectionText=setupRequired?"Speech setup needed":"Connected";UpdateConnection();}
        UpdateSetup();
        if(setupRequired&&!setupShown&&(phase=="choose"||phase=="error")){setupShown=true;OpenSetup();}
    }
    void OpenSetup(){
        Through(false);if(setupWindow!=null){setupWindow.Show();setupWindow.Activate();return;}
        var panel=new StackPanel { Margin=new Thickness(24,0,24,24) };
        setupWindow=new ModernWindow("Speech setup","One setup. Local captions, even offline.",panel){Owner=this,Width=540,Height=690,MinWidth=400,MinHeight=400};
        setupWindow.Closed+=(_,_)=>setupWindow=null;
        var intro=Theme.Text("Rayme includes NVIDIA CUDA and CPU speech runtimes. Auto prefers a compatible GPU, including RTX 4080, and safely falls back to CPU. No CUDA toolkit, developer tools or administrator account is needed. NVIDIA mode requires a compatible installed driver. Choose your existing ggml model or download Large-v3-Turbo.",14);intro.Margin=new Thickness(0,0,0,16);panel.Children.Add(intro);
        AddProcessingControls(panel);
        setupMessage=Theme.Text("Checking speech setup…",13,"#D8ECFF");Theme.Accessible(setupMessage,"Speech setup status");panel.Children.Add(Theme.Card(setupMessage));
        setupProgress=new ProgressBar { Minimum=0,Maximum=100,Height=8,Margin=new Thickness(0,0,0,16) };panel.Children.Add(setupProgress);
        panel.Children.Add(Theme.Text("Use an existing model",16,strong:true));
        panel.Children.Add(Theme.Text("Select a complete ggml .bin file. GGUF models and download shortcuts are not supported.",12,"#B9C7DC"));
        modelPath=new TextBox { Name="ModelPath",MinHeight=38,Margin=new Thickness(0,10,0,8),ToolTip="Existing local Whisper ggml model path" };Theme.Accessible(modelPath,"Existing model path");panel.Children.Add(modelPath);
        var existing=new WrapPanel();existing.Children.Add(Theme.Button("Browse…",(_,_)=>{var pick=new OpenFileDialog{Title="Choose a Whisper ggml model",Filter="Whisper ggml models (*.bin)|*.bin|All files (*.*)|*.*",CheckFileExists=true};if(pick.ShowDialog(setupWindow)==true)modelPath.Text=pick.FileName;},"\uE8B7"));
        useModel=Theme.Button("Use this model",async(_,_)=>await Send(new{type="setup",action="select",path=modelPath.Text}),style:"PrimaryButton");useModel.Name="UseModel";existing.Children.Add(useModel);panel.Children.Add(existing);
        panel.Children.Add(Theme.Text("Or download Large-v3-Turbo",16,strong:true));
        panel.Children.Add(Theme.Text("Recommended production model · approximately 1.63 GB download. Allow at least 2.2 GB of free disk space and 4 GB of free RAM. CPU inference can be slower on older PCs.",13,"#B9C7DC"));
        panel.Children.Add(Theme.Text("Official OpenAI Whisper weights converted by whisper.cpp, under the MIT License. Rayme verifies the published upstream hash before loading. Weights are downloaded only when you choose this action; the CI tiny model is never substituted.",12,"#B9C7DC"));
        downloadModel=Theme.Button("Download Large-v3-Turbo",async(_,_)=>await Send(new{type="setup",action="download",model="large-v3-turbo"}),"\uE896");downloadModel.Margin=new Thickness(0,12,0,8);panel.Children.Add(downloadModel);
        retrySetup=Theme.Button("Retry runtime check",async(_,_)=>await Send(new{type="setup",action="retry"}),"\uE72C");panel.Children.Add(retrySetup);
        panel.Children.Add(Theme.Text("Interrupted downloads can be retried. Completed valid models are preserved. Normal captions run fully offline after setup. Auto reports the backend actually initialized; GPU inference is verified only after a successful inference with NVIDIA process evidence.",12,"#B9C7DC"));
        panel.Children.Add(Theme.Button("Quit Rayme",(_,_)=>QuitRayme(),"\uE8BB"));
        UpdateSetup();setupWindow.Show();
    }
    void UpdateSetup(){
        if(setupWindow==null||setupState is not {} data)return;
        string phase=data.GetProperty("phase").GetString()??"";
        string message=data.GetProperty("message").GetString()??"";
        bool busy=phase is "checking" or "downloading" or "verifying" or "loading" or "switching";
        setupMessage!.Text=message;
        System.Windows.Automation.AutomationProperties.SetHelpText(setupMessage,message);
        double downloaded=data.TryGetProperty("downloaded",out var count)?count.GetDouble():0,total=data.TryGetProperty("total",out var size)?size.GetDouble():0;
        if(phase=="downloading")setupMessage.Text+=$"\n{downloaded/1000000:0} / {total/1000000:0} MB";
        setupProgress!.Value=total>0?Math.Clamp(downloaded*100/total,0,100):0;
        useModel!.IsEnabled=!busy;downloadModel!.IsEnabled=!busy;retrySetup!.IsEnabled=!busy;
    }
}
